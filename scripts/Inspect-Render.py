"""Independent FFmpeg decoding of renderer fixtures; compares preview, export and source samples."""
import argparse, hashlib, json, math
from pathlib import Path
import av
from PIL import Image, ImageChops, ImageStat

parser = argparse.ArgumentParser()
parser.add_argument('directory', type=Path)
args = parser.parse_args()
root = args.directory.resolve()
report = json.loads((root / 'result.json').read_text(encoding='utf-8-sig'))
checks = []
def check(name, passed, **data):
    checks.append(dict(name=name, passed=bool(passed), **data))
def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

evidence = next((r['evidence'] for r in report['results'] if r['passed'] and r['name'].startswith('actual media cuts')), None)
if evidence is None:
    raise SystemExit('No successful real-media export to inspect.')
w, h = evidence['width'], evidence['height']
expected = {p['index']: p for p in evidence['previews']}
export = root / 'edited-30.mp4'
with av.open(str(export)) as container:
    stream = container.streams.video[0]
    check('export codec and SDR metadata', stream.codec_context.name == 'h264' and len(container.streams.audio) == 0 and
        stream.codec_context.colorspace == 1 and stream.codec_context.color_range == 1 and stream.codec_context.color_primaries == 1 and stream.codec_context.color_trc == 1)
    frames = list(container.decode(stream))
    check('rational 30fps timestamps', len(frames) == 90 and all(abs(float(f.pts * f.time_base) - i / 30) < 0.000002 for i, f in enumerate(frames)), count=len(frames))
    for index, entry in expected.items():
        preview = Image.frombytes('RGBA', (w, h), (root / entry['file']).read_bytes(), 'raw', 'BGRA').convert('RGB')
        decoded = frames[index].to_image().convert('RGB')
        diff = ImageChops.difference(preview, decoded)
        mean = sum(ImageStat.Stat(diff).mean) / 3
        rms = math.sqrt(sum(v * v for v in ImageStat.Stat(diff).rms) / 3)
        psnr = 20 * math.log10(255 / max(rms, 1e-9))
        check(f'preview vs independently decoded export frame {index}', mean < 3 and psnr > 32, mean_absolute_channel_error=mean, psnr_db=psnr)
        preview.save(root / f'preview-{index}.png')
        decoded.save(root / f'export-{index}.png')
        # Independently select source frame at/before the mapped local PTS and sample the composed image.
        project_dir = next(p for p in (root / 'projects').iterdir() if json.loads((p / 'project.json').read_text())['name'] == 'Actual fixture render')
        segments = [json.loads(line) for line in (project_dir / 'segments.jsonl').read_text().splitlines()]
        plan = entry['frame']; source_us = plan['SourceUs']
        segment = next(s for s in segments if s['startUs'] <= source_us < s['endUs'])
        local_seconds = (source_us - segment['startUs']) / 1e6
        with av.open(str(project_dir / 'media' / segment['fileName'])) as source:
            chosen = None
            for f in source.decode(video=0):
                if float(f.pts * f.time_base) > local_seconds + 1e-9:
                    break
                chosen = f
            assert chosen is not None
            source_image = chosen.to_image().convert('RGB')
        sw, sh = source_image.size; crop = plan['SourceCrop']; dest = plan['Destination']
        errors = []
        for y in range(7, h, 13):
            for x in range(7, w, 13):
                if plan['CursorVisible'] and abs(x - plan['CursorPixel']['X']) < 35 and abs(y - plan['CursorPixel']['Y']) < 45:
                    continue
                dx = (x + .5 - dest['X']) / dest['Width']; dy = (y + .5 - dest['Y']) / dest['Height']
                if not 0 <= dx < 1 or not 0 <= dy < 1:
                    reference = (20, 24, 32)
                else:
                    sx = max(0, min(sw - 1, (crop['X'] + dx * crop['Width']) * sw - .5))
                    sy = max(0, min(sh - 1, (crop['Y'] + dy * crop['Height']) * sh - .5))
                    x0, y0 = int(sx), int(sy); fx, fy = sx - x0, sy - y0
                    corners = [source_image.getpixel((a, b)) for a, b in [(x0, y0), (min(sw-1, x0+1), y0), (x0, min(sh-1, y0+1)), (min(sw-1, x0+1), min(sh-1, y0+1))]]
                    reference = tuple(round((corners[0][c] * (1-fx) + corners[1][c] * fx) * (1-fy) + (corners[2][c] * (1-fx) + corners[3][c] * fx) * fy) for c in range(3))
                errors.extend(abs(a-b) for a,b in zip(reference, preview.getpixel((x,y))))
        mean = sum(errors) / len(errors)
        p99 = sorted(errors)[int(len(errors)*.99)]
        check(f'independent source time, crop and destination frame {index}', mean < 3 and p99 <= 12, mean_absolute_channel_error=mean, p99_channel_error=p99, source_us=source_us, source_file=segment['fileName'])

with av.open(str(root / 'edited-60.mp4')) as container:
    frames = list(container.decode(video=0))
    end = float((frames[-1].pts + frames[-1].duration) * frames[-1].time_base)
    check('60fps fractional tail', len(frames) == 31 and abs(end - .500123) < .0001 and all(abs(float(f.pts*f.time_base) - i/60) < .000002 for i,f in enumerate(frames)), frames=len(frames), duration_seconds=end)
output = dict(timestamp_source=report['timestamp'], scope='short ARM VM functional render checks; not performance or release certification', decoder='PyAV '+av.__version__,
    decoder_libraries={k: list(v) for k,v in av.library_versions.items()}, engine_sha256=report['engineSha256'], export_sha256=digest(export), checks=checks,
    passed=all(c['passed'] for c in checks))
(root / 'render-inspection.json').write_text(json.dumps(output, indent=2) + '\n')
print(json.dumps(output, indent=2))
raise SystemExit(0 if output['passed'] else 1)
