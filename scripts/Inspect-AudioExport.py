"""Independent decoding/signal audit for the controlled-PCM Windows render fixture.

This tests editing/mixing/AAC, not microphone acquisition or physical A/V latency.
"""
import argparse, array, hashlib, json, math, sys
from pathlib import Path
import av
RATE = 48_000


def amplitude(samples, start, frequency):
    data = samples[round(start * RATE):round(start * RATE) + 4800]
    if len(data) != 4800:
        return 0.0
    real = sum(x * math.cos(2 * math.pi * frequency * i / RATE) for i, x in enumerate(data))
    imaginary = sum(x * math.sin(2 * math.pi * frequency * i / RATE) for i, x in enumerate(data))
    return 2 * math.hypot(real, imaginary) / len(data)


def signal_checks(channels, system_only=False):
    checks = []
    for start in (.3, .7, 1.3, 1.8, 2.5):
        for channel, samples in enumerate(channels):
            base = (440 if start < 1 else 660) * (1 if channel == 0 else 1.25)
            mic, system = amplitude(samples, start, base), amplitude(samples, start, base * 2)
            mic_expected = 0 if system_only else 6000 / 32768 * .5
            system_expected = 6000 / 32768 * .25
            checks.append(dict(name=f'tones at {start}s channel {channel}',
                passed=abs(mic - mic_expected) < .006 and abs(system - system_expected) < .006,
                microphone_amplitude=mic, system_amplitude=system, microphone_expected=mic_expected, system_expected=system_expected))
    return checks


def compare_pcm(channels, reference):
    # Find encoder displacement at the edited tone transition. Coarse search plus
    # full-rate refinement avoids ambiguous phase-only estimates on a steady tone.
    a, b = channels[0], reference
    begin, end = int(.8 * RATE), int(1.2 * RATE)
    if len(a) < end + 4800 or len(b) < end:
        return dict(name='AAC edited PCM comparison', passed=False, error='Insufficient decoded/reference samples')
    def mse(lag, step):
        if begin + lag < 0 or end + lag > len(a) or end > len(b): return math.inf
        return sum((a[i + lag] - b[i]) ** 2 for i in range(begin, end, step)) / len(range(begin, end, step))
    coarse = min(range(-4800, 4801, 8), key=lambda lag: mse(lag, 16))
    lag = min(range(coarse - 8, coarse + 9), key=lambda candidate: mse(candidate, 1))
    rmse = math.sqrt(mse(lag, 1))
    return dict(name='AAC samples align with edited PCM around the cut', passed=abs(lag) <= 2400 and rmse < .012,
                delay_samples=lag, delay_ms=lag / RATE * 1000, aligned_rmse=rmse)


def inspect(root):
    checks, files = [], []
    for name, duration in [('audio-mixed.mp4', 3), ('audio-system-only.mp4', 3), ('audio-fractional.mp4', .500123), ('audio-all-muted.mp4', .500123)]:
        path = root / name
        try:
            files.append(dict(file=name, sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
            with av.open(str(path)) as video:
                decoded_video = list(video.decode(video=0)); fps = 30 if duration == 3 else 60
                checks.append(dict(name=name + ' full video decode and rational timestamps',
                    passed=len(decoded_video) == math.ceil(duration * fps) and all(abs(float(f.pts * f.time_base) - i / fps) < .000002 for i, f in enumerate(decoded_video)),
                    decoded_frames=len(decoded_video)))
            with av.open(str(path)) as container:
                muted = name == 'audio-all-muted.mp4'
                checks.append(dict(name=name + ' streams', passed=len(container.streams.video) == 1 and len(container.streams.audio) == (0 if muted else 1)))
                if muted: continue
                stream = container.streams.audio[0]
                frames = list(container.decode(stream))
                checks.append(dict(name=name + ' AAC stereo 48kHz', passed=stream.codec_context.name == 'aac' and stream.codec_context.sample_rate == RATE and stream.layout.name == 'stereo'))
                if not frames: raise ValueError('No decoded audio')
                channels = [[], []]; timestamps = []
                for frame in frames:
                    if frame.format.name != 'fltp' or frame.sample_rate != RATE or len(frame.planes) != 2: raise ValueError('Unexpected decoder PCM layout')
                    timestamps.append((float(frame.pts * frame.time_base), frame.samples / RATE))
                    for c in range(2):
                        plane = array.array('f', bytes(frame.planes[c]))
                        if sys.byteorder != 'little': plane.byteswap()
                        channels[c].extend(plane[:frame.samples])
                gap = max((abs(timestamps[i][0] - sum(timestamps[i - 1])) for i in range(1, len(timestamps))), default=0)
                end = sum(timestamps[-1])
                checks.append(dict(name=name + ' continuous PCM timestamps and tail', passed=abs(timestamps[0][0]) <= 1 / RATE and gap < .000002 and abs(end - duration) <= .05,
                    first_seconds=timestamps[0][0], max_gap_seconds=gap, end_seconds=end, expected_seconds=duration, frames=len(channels[0])))
                checks.append(dict(name=name + ' finite PCM', passed=all(math.isfinite(x) for channel in channels for x in channel)))
                if duration == 3: checks.extend(signal_checks(channels, name == 'audio-system-only.mp4'))
                if name == 'audio-mixed.mp4':
                    reference = array.array('h', (root / 'audio-mixed-reference.s16le').read_bytes())
                    if sys.byteorder != 'little': reference.byteswap()
                    checks.append(compare_pcm(channels, [v / 32768 for v in reference[::2]]))
        except Exception as error:
            checks.append(dict(name=name + ' full decode', passed=False, error=str(error)))
    return dict(scope='Controlled synthetic PCM on actual WGC video; editing/mixing/AAC only. Not microphone or physical A/V certification.',
                decoder=f'PyAV {av.__version__}', files=files, passed=all(c['passed'] for c in checks), checks=checks)


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('directory', type=Path); parser.add_argument('--output', type=Path)
    args = parser.parse_args(); root = args.directory.resolve(); report = inspect(root)
    output = args.output or root / 'audio-export-inspection.json'; output.write_text(json.dumps(report, indent=2, allow_nan=False))
    print(('PASS' if report['passed'] else 'FAIL') + f' {len(report["checks"])} independent AAC checks: {output}')
    for check in report['checks']:
        if not check['passed']: print(json.dumps(check))
    return 0 if report['passed'] else 1


if __name__ == '__main__': raise SystemExit(main())
