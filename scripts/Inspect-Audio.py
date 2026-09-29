"""Independent PCM/tone audit of disposable WASAPI test captures, not GA certification."""
import argparse
import array
import hashlib
import json
import math
from pathlib import Path
import re
import wave
from datetime import datetime, timezone


def tone_measurement(samples, expected):
    if not samples:
        raise ValueError("No audio samples")
    rms = math.sqrt(sum(x*x for x in samples)/len(samples))/32768
    def amplitude(hz):
        step=2*math.pi*hz/48000
        real=sum(x*math.cos(step*i) for i,x in enumerate(samples))
        imag=sum(x*math.sin(step*i) for i,x in enumerate(samples))
        return 2*math.hypot(real,imag)/len(samples)/32768
    measured=amplitude(expected)
    return {"expectedHz":expected,"rms":rms,"expectedAmplitude":measured,
            "expectedToneFraction":min(1,measured/(math.sqrt(2)*rms)) if rms else 0,
            "paused880HzAmplitude":amplitude(880)}


def inspect(capture, tones=False):
    folder=capture/"audio"
    info=json.loads((folder/"audio-info.json").read_text())
    if (info["schemaVersion"],info["sampleRate"],info["channels"],info["format"]) != (1,48000,2,"pcm_s16le"):
        raise ValueError("Unsupported audio format")
    expected=[role for role in ("microphone","system") if info[role]]
    if not expected: raise ValueError("No selected audio")
    counts={role:0 for role in expected};indices={role:0 for role in expected}
    mono={role:array.array("h") for role in expected};segments=[]
    raw=(folder/"audio-journal.jsonl").read_text()
    if not raw.endswith("\n"):raise ValueError("Uncommitted audio journal tail")
    for line in raw.splitlines():
        entry=json.loads(line);role=entry["track"]
        if role not in expected or entry["file"]!=f"{role}-{indices[role]:06}.wav" or not re.fullmatch(r"(microphone|system)-\d{6}\.wav",entry["file"]):
            raise ValueError("Unexpected or unsafe audio source")
        path=folder/entry["file"]
        with wave.open(str(path),"rb") as source:
            if (source.getnchannels(),source.getsampwidth(),source.getframerate(),source.getcomptype())!=(2,2,48000,"NONE"):
                raise ValueError("WAV header profile mismatch")
            frames=source.getnframes();payload=source.readframes(frames)
            if len(payload)!=frames*4 or frames!=entry["frames"] or not 0<frames<=240000:
                raise ValueError("Truncated WAV or mismatched frame count")
        if counts[role]!=entry["startFrame"]:raise ValueError("Audio timeline contains a gap or overlap")
        samples=array.array("h",payload);mono[role].extend(samples[::2]);counts[role]+=frames;indices[role]+=1
        segments.append({"file":entry["file"],"frames":frames,"sha256":hashlib.sha256(path.read_bytes()).hexdigest()})
    video=[json.loads(l) for l in (capture/"capture-journal.jsonl").read_text().splitlines()]
    duration_us=video[-1]["endUs"]
    checks={"allSelectedTracksPresent":all(counts.values()),
            "tailLengthDifferenceAtMost50ms":all(abs(n/48000-duration_us/1000000)<=.05 for n in counts.values())}
    clock_residuals = None
    performance_path=folder/"audio-performance.json"
    if performance_path.exists():
        performance=json.loads(performance_path.read_text())
        if performance["schemaVersion"] >= 2:
            clock_residuals={role:performance["tracks"][role]["maximumClockResidual100ns"] for role in expected}
            if any(type(value) is not int or value<0 for value in clock_residuals.values()):raise ValueError("Invalid clock residual metric")
            checks["reportedClockResidualAtMost50ms"]=all(value<=500000 for value in clock_residuals.values())
    windows=[]
    maximum_silence = None
    if tones:
        if expected!=["system"]:raise ValueError("Tone fixture uses system-only capture")
        # Steady regions exclude startup and the deliberately changed/pause boundary.
        # These are fixed checks, not a fit to the captured result's peak frequency.
        for start in [1.5,2,2.5,3,5,5.5,6,6.5,7,7.5,8,8.5]:
            first=round(start*48000)
            samples=mono["system"][first:first+4800]
            if len(samples)!=4800:raise ValueError("Tone fixture is shorter than the audit interval")
            windows.append({"startSeconds":start,**tone_measurement(samples,440 if start<4 else 660)})
        checks["continuousTestTonePresent"]=all(w["rms"]>.01 for w in windows)
        checks["expectedToneFractionAtLeast80Percent"]=all(w["expectedToneFraction"]>=.8 for w in windows)
        checks["pausedToneExcluded"]=all(w["paused880HzAmplitude"]<.005 for w in windows)
        run=longest=0
        for sample in mono["system"]:
            run=run+1 if abs(sample)<=1 else 0
            longest=max(longest,run)
        maximum_silence=longest/48000
        checks["noTestToneSilenceAtLeast50ms"]=longest<2400
    return {"timestamp":datetime.now(timezone.utc).isoformat(),"scope":"Short functional PCM and synthetic-tone audit. Does not certify microphone, fine A/V synchronization, long-term drift, hardware performance, or production readiness.",
            "passed":all(checks.values()),"checks":checks,"audioFrames":counts,"videoDurationUs":duration_us,"segments":segments,"toneWindows":windows,"maximumTestToneSilenceSeconds":maximum_silence,"maximumClockResidual100ns":clock_residuals}


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture",type=Path);parser.add_argument("--fixture-tones",action="store_true")
    parser.add_argument("--output",type=Path)
    args=parser.parse_args();report=inspect(args.capture,args.fixture_tones)
    (args.output or args.capture/"audio-inspection.json").write_text(json.dumps(report,indent=2)+"\n")
    print("PASS" if report["passed"] else "FAIL",{name:value for name,value in report["checks"].items() if not value})
    raise SystemExit(0 if report["passed"] else 1)
