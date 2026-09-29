"""Correlate decoded fixture IDs with native capture timing, without relaxing media gates.

This is diagnostic evidence from a disposable probe. QPC intervals include OS/VM
scheduling; they do not identify GPU execution time or certify physical hardware.
"""
import argparse
import importlib.util
import json
from pathlib import Path
from datetime import datetime, timezone

import av

_spec = importlib.util.spec_from_file_location("media_inspector", Path(__file__).with_name("Inspect-Media.py"))
_media = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_media)


def read_trace(raw):
    rows, tail = [], None
    lines = raw.splitlines(keepends=True)
    for index, line in enumerate(lines):
        if not line.strip(): continue
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError:
            # Abrupt termination can split only the last diagnostic record. Keep
            # that fact in the report; missing committed-frame traces still fail.
            if index != len(lines)-1 or line.endswith(('\n', '\r')): raise
            tail = {"line": index+1, "utf8Bytes": len(line.encode('utf-8')),
                    "scope": "Unterminated diagnostic tail retained in original file; excluded from parsed rows."}
    return rows, tail


def correlate(frames, traces, paints, fps):
    by_index = {row["frame"]: row for row in traces}
    checks = {"uniqueTraceFrameIndices": len(by_index) == len(traces),
              "allCommittedFramesHaveTrace": True, "decodedPtsMatchFrameGrid": True,
              "allDecodedIdsValid": True, "sameSourceKeyHasSameDecodedId": True,
              "cachedFramesHaveZeroReadback": True, "qpcPhaseOrder": True}
    qpc_available = all("readBeginQpc100ns" in row for row in traces) and bool(traces)
    cache_available = all("pixelsCached" in row for row in traces) and bool(traces)
    if not qpc_available: checks["qpcPhaseOrder"] = None
    if not cache_available: checks["cachedFramesHaveZeroReadback"] = None
    known_sources, joined = {}, []
    for frame in frames:
        index = round(frame["ptsSeconds"] * fps)
        checks["decodedPtsMatchFrameGrid"] &= abs(index / fps - frame["ptsSeconds"]) < .0001
        checks["allDecodedIdsValid"] &= frame["fixtureId"] is not None
        trace = by_index.get(index)
        if trace is None:
            checks["allCommittedFramesHaveTrace"] = False
            continue
        row = {**frame, **trace}
        key = (trace.get("sourceEpoch"), trace["sourceSequence"])
        if key in known_sources:
            checks["sameSourceKeyHasSameDecodedId"] &= known_sources[key] == frame["fixtureId"]
        known_sources[key] = frame["fixtureId"]
        if trace.get("pixelsCached"):
            checks["cachedFramesHaveZeroReadback"] &= trace["readbackUs"] == 0
        if qpc_available:
            # WGC's SystemRelativeTime is preserved separately, including negative
            # apparent source ages. Only our own QPC calls are ordered here.
            phases = [trace[name] for name in ("sourceAcquiredQpc100ns", "sourcePublishedQpc100ns",
                       "readBeginQpc100ns", "readEndQpc100ns", "encodeBeginQpc100ns", "encodeEndQpc100ns")]
            checks["qpcPhaseOrder"] &= all(a <= b for a, b in zip(phases, phases[1:]))
            row["sourcePublishWorkUs"] = (phases[1] - phases[0]) / 10
            row["publishedAgeAtReadUs"] = (phases[2] - phases[1]) / 10
        joined.append(row)
    groups = []
    for row in joined:
        if not groups or groups[-1][-1]["fixtureId"] != row["fixtureId"]:
            groups.append([])
        groups[-1].append(row)
    long_runs = []
    for group_index, group in enumerate(groups):
        first, last = group[0], group[-1]
        duration = last["ptsSeconds"] + last["durationSeconds"] - first["ptsSeconds"]
        if duration < .1 - 1e-7:
            continue
        next_id = groups[group_index + 1][0]["fixtureId"] if group_index + 1 < len(groups) else None
        reference = [p for p in paints if first["fixtureId"] is not None and p["frame"] >= first["fixtureId"]
                     and (next_id is not None and p["frame"] <= next_id)]
        gaps = [(b["qpc100ns"]-a["qpc100ns"])/10 for a, b in zip(reference, reference[1:])]
        long_runs.append({"fixtureId": first["fixtureId"], "nextFixtureId": next_id,
                         "startSeconds": first["ptsSeconds"], "durationSeconds": duration,
                         "decodedFrames": len(group), "referencePaintsUntilNextDecodedId": len(reference),
                         "maximumReferencePaintGapUs": max(gaps, default=None),
                         "maximumReadbackUs": max(r["readbackUs"] for r in group),
                         "maximumRolloverUs": max(r["rolloverUs"] for r in group),
                         "maximumEncodeUs": max(r["encodeUs"] for r in group),
                         "maximumPublishedAgeAtReadUs": max((r.get("publishedAgeAtReadUs", 0) for r in group)) if qpc_available else None,
                         "frames": group})
    return {"diagnosticChecksPassed": all(value is not False for value in checks.values()), "checks": checks,
            "decodedFrames": len(frames), "traceFrames": len(traces), "matchedFrames": len(joined),
            "qpcBreakdownAvailable": qpc_available, "pixelCacheEvidenceAvailable": cache_available,
            "committedPixelCacheHits": sum(bool(r.get("pixelsCached")) for r in joined) if cache_available else None,
            "negativeWgcSourceAgeSamples": sum(r["sourceAgeUs"] < 0 for r in joined),
            "maximumReadbackUs": max((r["readbackUs"] for r in joined), default=0),
            "maximumPublishedAgeAtReadUs": max((r.get("publishedAgeAtReadUs", 0) for r in joined), default=0) if qpc_available else None,
            "maximumRepeatedSeconds": max((g[-1]["ptsSeconds"] + g[-1]["durationSeconds"] - g[0]["ptsSeconds"] for g in groups), default=0),
            "runsAtLeast100ms": long_runs}


def inspect(root):
    info = json.loads((root / "capture-info.json").read_text())
    barcode = json.loads((root / "probe-fixture.json").read_text())["barcode"]
    traces, truncated_tail = read_trace((root / "capture-timings.jsonl").read_text())
    paints = [json.loads(line) for line in (root / "fixture-paints.jsonl").read_text().splitlines() if line.strip()]
    frames = []
    for index, line in enumerate((root / "capture-journal.jsonl").read_text().splitlines()):
        entry = json.loads(line)
        if entry["file"] != f"screen-{index:06}.mp4":
            raise ValueError("Unsafe or unordered capture filename")
        with av.open(str(root / entry["file"])) as container:
            for frame in container.decode(video=0):
                if frame.pts is None or frame.time_base is None or frame.is_corrupt:
                    raise ValueError("Corrupt frame or missing timestamp")
                frames.append({"ptsSeconds": entry["startUs"] / 1_000_000 + float(frame.pts * frame.time_base),
                               "durationSeconds": float(frame.duration * frame.time_base),
                               "fixtureId": _media.fixture_frame_id(frame.to_image(), barcode)})
    if not frames:
        raise ValueError("No committed frames")
    return {"schemaVersion": 1, "timestamp": datetime.now(timezone.utc).isoformat(),
            "truncatedTraceTail": truncated_tail,
            "scope": "Decoded fixture and native QPC correlation. Diagnostic checks are not media quality or release gates. Reference paint is not display scanout; QPC intervals include VM/OS scheduling.",
            **correlate(frames, traces, paints, info["fps"])}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture_directory", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = inspect(args.capture_directory.resolve())
    output = args.output or args.capture_directory / "capture-timing-inspection.json"
    output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({key: value for key, value in report.items() if key != "runsAtLeast100ms"}, indent=2))
    raise SystemExit(0 if report["diagnosticChecksPassed"] else 1)
