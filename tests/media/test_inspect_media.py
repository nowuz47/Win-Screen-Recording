"""Independent decoder checks must reject mid-segment blanks and boundary stalls."""
import importlib.util
import json
from pathlib import Path
from fractions import Fraction
import tempfile
import unittest

import av
from PIL import Image, ImageDraw

module = importlib.util.spec_from_file_location("inspect_media", Path(__file__).resolve().parents[2] / "scripts/Inspect-Media.py")
inspection = importlib.util.module_from_spec(module)
module.loader.exec_module(inspection)


BARCODE = {"x":28,"y":300,"cellWidth":12,"cellHeight":18}


def draw_frame_id(draw, number, barcode=BARCODE):
    checksum = (number ^ (number >> 8) ^ (number >> 16) ^ (number >> 24) ^ 0xa5) & 255
    bits = [1,0,1,0] + [(number >> i) & 1 for i in range(32)] + [(checksum >> i) & 1 for i in range(8)]
    x,y,w,h = (barcode[k] for k in ("x","y","cellWidth","cellHeight"))
    for i,bit in enumerate(bits):
        draw.rectangle((x+i*w,y,x+(i+1)*w-1,y+h-1),fill="white" if bit else "black")


def segment(root, number, blank_frame=None, ids=None):
    path = root / f"screen-{number:06}.mp4"
    with av.open(str(path), "w") as out:
        stream = out.add_stream("libx264", rate=30)
        stream.width, stream.height, stream.pix_fmt = 640, 360, "yuv420p"
        stream.options = {"crf": "10", "preset": "ultrafast", "tune": "zerolatency"}
        stream.codec_context.colorspace = 1
        stream.codec_context.color_primaries = 1
        stream.codec_context.color_trc = 1
        stream.codec_context.color_range = 1
        for index in range(len(ids) if ids is not None else 3):
            picture = Image.new("RGB", (640, 360), (18, 28, 35))
            draw = ImageDraw.Draw(picture)
            if index != blank_frame:
                for x, color in zip((80,220,360,500), ((238,78,84),(34,192,155),(62,133,246),(246,196,55))):
                    draw.rectangle((x-30,180,x+30,250), fill=color)
            if ids is not None: draw_frame_id(draw,ids[index])
            frame = av.VideoFrame.from_image(picture).reformat(format="yuv420p", dst_colorspace="ITU709")
            frame.pts, frame.time_base = index, Fraction(1,30)
            for packet in stream.encode(frame): out.mux(packet)
        for packet in stream.encode(): out.mux(packet)


class MediaInspectionTests(unittest.TestCase):
    def test_frame_id_checksum_rejects_a_corrupted_payload(self):
        picture = Image.new("RGB", (600, 40))
        draw = ImageDraw.Draw(picture)
        number = 108001
        checksum = (number ^ (number >> 8) ^ (number >> 16) ^ (number >> 24) ^ 0xa5) & 255
        bits = [1,0,1,0] + [(number >> i) & 1 for i in range(32)] + [(checksum >> i) & 1 for i in range(8)]
        for i,bit in enumerate(bits): draw.rectangle((i*12,0,i*12+11,17),fill="white" if bit else "black")
        barcode = {"x":0,"y":0,"cellWidth":12,"cellHeight":18}
        self.assertEqual(inspection.fixture_frame_id(picture,barcode),number)
        draw.rectangle((4*12,0,4*12+11,17),fill="black" if bits[4] else "white")
        self.assertIsNone(inspection.fixture_frame_id(picture,barcode))

    def capture(self, root, second_start=100000, blank=None):
        (root / "capture-info.json").write_text(json.dumps({"width":640,"height":360,"fps":30}))
        entries = [{"file":f"screen-{i:06}.mp4", "startUs":start, "endUs":start+100000} for i,start in enumerate((0,second_start))]
        (root / "capture-journal.jsonl").write_text("\n".join(json.dumps(x) for x in entries))
        segment(root,0,blank); segment(root,1)
        return inspection.inspect(root,True)

    def test_valid_multi_segment_media(self):
        with tempfile.TemporaryDirectory() as folder:
            result = self.capture(Path(folder))
            self.assertTrue(result["passed"], result)
            self.assertEqual(result["decodedFrames"],6)

    def test_blank_middle_frame_is_not_hidden_by_valid_first_frame(self):
        with tempfile.TemporaryDirectory() as folder:
            result = self.capture(Path(folder),blank=1)
            self.assertFalse(result["passed"])
            self.assertTrue(result["segments"][0]["checks"]["fixtureColorErrorAtMost6"])
            self.assertEqual(result["segments"][0]["allFrameColors"]["failedFrames"],1)

    def test_boundary_gap_is_not_hidden_by_locally_continuous_segments(self):
        with tempfile.TemporaryDirectory() as folder:
            result = self.capture(Path(folder),second_start=400000)
            self.assertFalse(result["passed"])
            self.assertTrue(all(s["checks"]["noGapAtLeast250ms"] for s in result["segments"]))
            self.assertFalse(result["continuityChecks"]["noGapAtLeast250msAcrossSegments"])

    def identified_capture(self, root, ids, paints=None):
        (root / "capture-info.json").write_text(json.dumps({"width":640,"height":360,"fps":30}))
        (root / "probe-fixture.json").write_text(json.dumps({"schemaVersion":2,"barcode":BARCODE}))
        entries=[]
        start=0
        for i,numbers in enumerate(ids):
            end=start+len(numbers)*1000000//30
            entries.append({"file":f"screen-{i:06}.mp4","startUs":start,"endUs":end})
            segment(root,i,ids=numbers)
            start=end
        (root / "capture-journal.jsonl").write_text("\n".join(json.dumps(e) for e in entries))
        if paints is not None:
            (root / "fixture-paints.jsonl").write_text("\n".join(json.dumps({"frame":i,"qpc100ns":q}) for i,q in enumerate(paints)))
        return inspection.inspect(root,True)

    def test_valid_encoded_frame_ids_and_reference(self):
        with tempfile.TemporaryDirectory() as folder:
            result=self.identified_capture(Path(folder),[list(range(6)),list(range(6,12))],list(range(0,4000000,333333)))
            self.assertTrue(result["passed"],result)
            self.assertEqual(result["fixtureFrameIds"]["frames"],12)
            self.assertTrue(result["fixtureReference"]["available"])

    def test_repeated_frame_spanning_segments_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            result=self.identified_capture(Path(folder),[[42]*6,[42]*6])
            self.assertFalse(result["passed"])
            self.assertTrue(result["continuityChecks"]["noGapAtLeast250msAcrossSegments"])
            self.assertFalse(result["continuityChecks"]["noRepeatedFixtureFrameAtLeast250ms"])

    def test_valid_checksum_cannot_hide_backward_frame_ids(self):
        with tempfile.TemporaryDirectory() as folder:
            result=self.identified_capture(Path(folder),[[20,21,22],[19,20,21]])
            self.assertFalse(result["passed"])
            self.assertTrue(result["continuityChecks"]["allFixtureFrameIdsValid"])
            self.assertEqual(result["fixtureFrameIds"]["backwards"],1)

    def test_stalled_reference_cannot_certify_otherwise_valid_video(self):
        with tempfile.TemporaryDirectory() as folder:
            result=self.identified_capture(Path(folder),[list(range(6)),list(range(6,12))],[0,333333,3000000])
            self.assertFalse(result["passed"])
            self.assertTrue(all(all(s["checks"].values()) for s in result["segments"]))
            self.assertFalse(result["continuityChecks"]["fixtureReferenceNoGapAtLeast250ms"])

    def test_decode_count_mismatch_fails_complete_report(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder)
            self.capture(root)
            (root / "probe-result.json").write_text(json.dumps({"encodedFrames":7}))
            result=inspection.inspect(root,True)
            self.assertFalse(result["passed"])
            self.assertFalse(result["frameCountsMatch"])


if __name__ == "__main__":
    unittest.main()
