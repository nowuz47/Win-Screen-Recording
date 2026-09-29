"""Decode actual capture segments with FFmpeg, independent of the Windows MF verifier.

Run only on test capture directories. --fixture also checks the four color swatches
drawn by capture_probe.cpp. This is a short regression test, not a release gate pass.
"""
import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path

import av


def fixture_frame_id(picture, barcode):
    x, y, w, h = (barcode[k] for k in ("x", "y", "cellWidth", "cellHeight"))
    if min(x,y) < 0 or w < 4 or h < 4 or x+44*w > picture.width or y+h > picture.height:
        raise ValueError("Fixture barcode bounds are invalid")
    values = [sum(picture.getpixel((x+i*w+w//2,y+h//2))[:3])/3 for i in range(44)]
    if any(40 < value < 210 for value in values): return None
    bits = [int(value >= 210) for value in values]
    if bits[:4] != [1,0,1,0]: return None
    number = sum(bit << i for i,bit in enumerate(bits[4:36]))
    checksum = sum(bit << i for i,bit in enumerate(bits[36:]))
    expected = (number ^ (number >> 8) ^ (number >> 16) ^ (number >> 24) ^ 0xa5) & 255
    return number if checksum == expected else None


def inspect(root: Path, fixture: bool) -> dict:
    info = json.loads((root / "capture-info.json").read_text())
    journal = (root / "capture-journal.jsonl").read_text().splitlines()
    reports = []
    previous_end_us = 0
    previous_absolute_pts = None
    maximum_global_gap = 0.0
    global_monotonic = True
    fixture_info = root / "probe-fixture.json"
    barcode = json.loads(fixture_info.read_text()).get("barcode") if fixture and fixture_info.exists() else None
    previous_id = None
    run_start = 0.0
    id_count = id_invalid = id_repeats = id_backwards = 0
    maximum_same_id_seconds = 0.0
    for index, line in enumerate(journal):
        entry = json.loads(line)
        name = entry["file"]
        if not re.fullmatch(r"screen-\d{6}\.mp4", name) or name != f"screen-{index:06}.mp4":
            raise ValueError("Unsafe or unordered capture filename")
        path = root / name
        frames = []
        first_image = None
        color_failures = []
        color_failure_count = 0
        maximum_color_error = 0
        tags = None
        checks = {"nonOverlappingJournalRange": entry["startUs"] >= previous_end_us and entry["endUs"] > entry["startUs"]}
        previous_end_us = entry["endUs"]
        with av.open(str(path)) as container:
            if len(container.streams.video) != 1 or len(container.streams.audio) != 0:
                raise ValueError("Expected one video stream for the current silent capture profile")
            stream = container.streams.video[0]
            checks["geometry"] = stream.width == info["width"] and stream.height == info["height"]
            checks["codec"] = stream.codec_context.name == "h264"
            for frame in container.decode(stream):
                if frame.pts is None or frame.time_base is None or frame.is_corrupt:
                    raise ValueError("Corrupt frame or missing presentation timestamp")
                if first_image is None:
                    first_image = frame.to_image()
                    tags = {"matrix": frame.colorspace, "primaries": frame.color_primaries,
                            "transfer": frame.color_trc, "range": frame.color_range}
                absolute_pts = entry["startUs"] / 1_000_000 + float(frame.pts * frame.time_base)
                if previous_absolute_pts is not None:
                    gap = absolute_pts - previous_absolute_pts
                    maximum_global_gap = max(maximum_global_gap, gap)
                    global_monotonic = global_monotonic and gap > 0
                previous_absolute_pts = absolute_pts
                if fixture:
                    picture = first_image if not frames else frame.to_image()
                    if picture.width < 600 or picture.height < 300:
                        raise ValueError("Fixture dimensions are incompatible with swatch sample points")
                    expected = [(238, 78, 84), (34, 192, 155), (62, 133, 246), (246, 196, 55)]
                    actual = [picture.getpixel((x, 220))[:3] for x in (80, 220, 360, 500)]
                    error = max(abs(a-b) for pixel, target in zip(actual, expected) for a,b in zip(pixel,target))
                    maximum_color_error = max(maximum_color_error, error)
                    if error > 6:
                        color_failure_count += 1
                        if len(color_failures) < 20:
                            color_failures.append({"frame": len(frames), "ptsSeconds": float(frame.pts * frame.time_base), "maximumChannelError": error})
                        if color_failure_count == 1:
                            picture.save(root / f"{path.stem}-first-color-failure.png")
                    if barcode:
                        number = fixture_frame_id(picture,barcode)
                        id_count += 1
                        if number is None:
                            id_invalid += 1
                            previous_id = None
                        else:
                            if previous_id == number: id_repeats += 1
                            else:
                                if previous_id is not None and number < previous_id: id_backwards += 1
                                run_start = absolute_pts
                            previous_id = number
                            maximum_same_id_seconds = max(maximum_same_id_seconds,
                                absolute_pts+float(frame.duration*frame.time_base)-run_start)
                frames.append((float(frame.pts * frame.time_base), float(frame.duration * frame.time_base)))
            if not frames:
                raise ValueError("No decoded frames")
            gaps = [b[0] - a[0] for a, b in zip(frames, frames[1:])]
            checks["monotonicTimestamps"] = all(gap > 0 for gap in gaps)
            checks["firstFrameAtZero"] = abs(frames[0][0]) <= .0001
            checks["noGapAtLeast250ms"] = max(gaps, default=0) < .25
            # FFmpeg color enum 1 denotes BT.709 and MPEG limited range for these fields.
            checks["bt709Metadata"] = tags == {"matrix": 1, "primaries": 1, "transfer": 1, "range": 1}
            expected_duration = (entry["endUs"] - entry["startUs"]) / 1_000_000
            end = frames[-1][0] + frames[-1][1]
            checks["durationWithinOneFrame"] = abs(end - expected_duration) <= 1 / info["fps"] + .0001
            swatches = None
            if fixture:
                if first_image.width < 600 or first_image.height < 300:
                    raise ValueError("Fixture dimensions are incompatible with swatch sample points")
                expected = [(238, 78, 84), (34, 192, 155), (62, 133, 246), (246, 196, 55)]
                actual = [first_image.getpixel((x, 220))[:3] for x in (80, 220, 360, 500)]
                error = max(abs(a - b) for pixel, target in zip(actual, expected) for a, b in zip(pixel, target))
                checks["fixtureColorErrorAtMost6"] = error <= 6
                checks["allFixtureFramesColorErrorAtMost6"] = color_failure_count == 0
                swatches = {"expected": expected, "actual": actual, "maximumChannelError": error}
                first_image.save(root / f"{path.stem}-inspection.png")
            with path.open("rb") as media:
                digest = hashlib.file_digest(media, "sha256").hexdigest()
            reports.append({"file": name, "sha256": digest, "decodedFrames": len(frames),
                            "firstPtsSeconds": frames[0][0], "decodedEndSeconds": end,
                            "journalDurationSeconds": expected_duration, "maximumGapSeconds": max(gaps, default=0),
                            "allFrameColors": {"checked": fixture, "maximumChannelError": maximum_color_error if fixture else None,
                                               "failedFrames": color_failure_count if fixture else None, "firstFailures": color_failures},
                            "colorTags": tags, "swatches": swatches, "checks": checks})
    if not reports:
        raise ValueError("No committed segments")
    encoded = None
    if (root / "probe-result.json").exists():
        encoded = json.loads((root / "probe-result.json").read_text())["encodedFrames"]
    decoded = sum(r["decodedFrames"] for r in reports)
    counts_match = encoded is None or decoded == encoded
    continuity = {"monotonicAcrossSegments": global_monotonic, "noGapAtLeast250msAcrossSegments": maximum_global_gap < .25 - .0000001}
    if barcode:
        continuity.update({"allFixtureFrameIdsValid": id_invalid == 0, "fixtureFrameIdsNeverBackwards": id_backwards == 0,
                           "noRepeatedFixtureFrameAtLeast250ms": maximum_same_id_seconds < .25 - .0000001})
    reference = {"available": False}
    paint_log = root / "fixture-paints.jsonl"
    if barcode and paint_log.exists():
        paints = [json.loads(line) for line in paint_log.read_text().splitlines() if line.strip()]
        gaps = [(b["qpc100ns"]-a["qpc100ns"])/10_000_000 for a,b in zip(paints,paints[1:])]
        reference = {"available": True, "paints": len(paints), "maximumPaintGapSeconds": max(gaps, default=0),
                     "monotonic": all(gap > 0 for gap in gaps),
                     "scope": "Paint completion timestamps in the disposable fixture; reference stalls cannot be attributed solely to capture."}
        continuity["fixtureReferenceNoGapAtLeast250ms"] = len(paints) >= 2 and reference["monotonic"] and max(gaps,default=0) < .25 - .0000001
    return {"timestamp": datetime.now(timezone.utc).isoformat(), "decoder": f"PyAV {av.__version__}",
            "libraries": av.library_versions, "scope": "short fixture regression; does not certify G02-G05",
            "passed": counts_match and all(continuity.values()) and all(all(r["checks"].values()) for r in reports),
            "continuityChecks": continuity, "maximumGlobalGapSeconds": maximum_global_gap,
            "fixtureReference": reference,
            "fixtureFrameIds": {"checked": bool(barcode), "frames": id_count, "invalid": id_invalid,
                                "repeated": id_repeats, "backwards": id_backwards, "maximumRepeatedSeconds": maximum_same_id_seconds,
                                "scope": "Observed encoded frame ID continuity; reference paint scheduling and app-attributed drop rate require separate analysis."},
            "encodedFrames": encoded, "decodedFrames": decoded, "frameCountsMatch": counts_match, "segments": reports}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture_directory", type=Path)
    parser.add_argument("--fixture", action="store_true")
    parser.add_argument("--output", type=Path, help="Write a separate audit without replacing earlier evidence")
    args = parser.parse_args()
    report = inspect(args.capture_directory.resolve(), args.fixture)
    (args.output or args.capture_directory / "media-inspection.json").write_text(json.dumps(report, indent=2) + "\n")
    for segment in report["segments"]:
        failed = [name for name, passed in segment["checks"].items() if not passed]
        print(segment["file"], "PASS" if not failed else "FAIL", ", ".join(failed))
    print("Whole capture:", "PASS" if report["passed"] else "FAIL",
          f"max timestamp gap {report['maximumGlobalGapSeconds'] * 1000:.2f}ms",
          ", ".join(name for name,passed in report["continuityChecks"].items() if not passed))
    if not report["frameCountsMatch"]: print("Encoded and decoded frame counts differ")
    raise SystemExit(0 if report["passed"] else 1)
