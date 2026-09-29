import array
import importlib.util
import json
import math
from pathlib import Path
import tempfile
import unittest
import wave

spec=importlib.util.spec_from_file_location("inspect_audio",Path(__file__).resolve().parents[2]/"scripts/Inspect-Audio.py")
audit=importlib.util.module_from_spec(spec);spec.loader.exec_module(audit)


def fixture(root, defect=None):
    folder=root/"audio";folder.mkdir()
    (folder/"audio-info.json").write_text(json.dumps(dict(schemaVersion=1,sampleRate=48000,channels=2,format="pcm_s16le",microphone=False,system=True)))
    records=[]
    for part in range(2):
        values=array.array("h")
        for i in range(240000):
            t=part*5+i/48000
            hz=440 if t<4 else 660
            if defect=="paused-tone" and 6.5<=t<6.6:hz=880
            value=0 if defect=="dropout" and 1<=t<1.1 else round(math.sin(2*math.pi*hz*t)*1638)
            values.extend([value,value])
        name=f"system-{part:06}.wav"
        with wave.open(str(folder/name),"wb") as output:
            output.setparams((2,2,48000,0,"NONE","not compressed"));output.writeframes(values.tobytes())
        records.append(dict(track="system",file=name,startFrame=part*240000,frames=240000))
    (folder/"audio-journal.jsonl").write_text("".join(json.dumps(x)+"\n" for x in records))
    (root/"capture-journal.jsonl").write_text(json.dumps(dict(file="screen-000000.mp4",startUs=0,endUs=10000000))+"\n")


class AudioInspectorTests(unittest.TestCase):
    def test_intact_tones_and_lengths_pass(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);fixture(root)
            result=audit.inspect(root,True)
            self.assertTrue(result["passed"],result)
            self.assertEqual(result["audioFrames"]["system"],480000)

    def test_paused_tone_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);fixture(root,"paused-tone")
            result=audit.inspect(root,True)
            self.assertFalse(result["passed"])
            self.assertFalse(result["checks"]["pausedToneExcluded"])

    def test_silence_outside_spectral_windows_is_still_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);fixture(root,"dropout")
            result=audit.inspect(root,True)
            self.assertFalse(result["passed"])
            self.assertTrue(result["checks"]["expectedToneFractionAtLeast80Percent"])
            self.assertFalse(result["checks"]["noTestToneSilenceAtLeast50ms"])

    def test_truncated_pcm_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);fixture(root)
            with (root/"audio/system-000000.wav").open("r+b") as data:data.truncate(100)
            with self.assertRaises(ValueError):audit.inspect(root,True)

    def test_good_tone_cannot_hide_excessive_reported_clock_residual(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);fixture(root)
            for residual,passed in [(500000,True),(500001,False)]:
                (root/"audio/audio-performance.json").write_text(json.dumps(dict(schemaVersion=2,tracks=dict(system=dict(maximumClockResidual100ns=residual)))))
                result=audit.inspect(root,True)
                self.assertTrue(result["checks"]["expectedToneFractionAtLeast80Percent"])
                self.assertEqual(result["checks"]["reportedClockResidualAtMost50ms"],passed)
                self.assertEqual(result["passed"],passed)


if __name__=="__main__":unittest.main()
