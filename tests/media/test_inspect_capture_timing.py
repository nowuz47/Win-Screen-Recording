import copy
import importlib.util
import json
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("capture_timing", Path(__file__).resolve().parents[2] / "scripts/Inspect-CaptureTiming.py")
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


def sample():
    frames = [dict(ptsSeconds=i/30, durationSeconds=1/30, fixtureId=10+i//2) for i in range(4)]
    traces = [dict(frame=i, sourceSequence=1+i//2, sourceEpoch=1, pixelsCached=bool(i%2),
                   sourceAgeUs=1000, readbackUs=0 if i%2 else 100, rolloverUs=1, encodeUs=300,
                   sourceAcquiredQpc100ns=10000+i//2*666666, sourcePublishedQpc100ns=11000+i//2*666666,
                   readBeginQpc100ns=12000+i*333333, readEndQpc100ns=13000+i*333333,
                   encodeBeginQpc100ns=14000+i*333333, encodeEndQpc100ns=17000+i*333333) for i in range(4)]
    return frames, traces, []


class CaptureTimingTests(unittest.TestCase):
    def test_only_incomplete_final_diagnostic_record_is_reported(self):
        rows, tail = audit.read_trace('{"frame":0}\n{"frame":')
        self.assertEqual(rows, [{'frame': 0}])
        self.assertEqual(tail['line'], 2)
        for raw in ('{"frame":0}\n{"frame":\n', '{"frame":\n{"frame":1}\n'):
            with self.assertRaises(json.JSONDecodeError): audit.read_trace(raw)

    def test_cache_and_qpc_match(self):
        r = audit.correlate(*sample(), 30)
        self.assertTrue(r['diagnosticChecksPassed'])
        self.assertEqual(r['committedPixelCacheHits'], 2)

    def test_timestamp_gap_counts_toward_frozen_frame(self):
        frames, traces, paints = sample()
        frames[1]['durationSeconds'] = 8/30
        frames[2]['ptsSeconds'] = 9/30
        frames[3]['ptsSeconds'] = 10/30
        traces[2]['frame'], traces[3]['frame'] = 9, 10
        paints = [dict(frame=10, qpc100ns=0), dict(frame=11, qpc100ns=1000000)]
        r = audit.correlate(frames, traces, paints, 30)
        self.assertAlmostEqual(r['maximumRepeatedSeconds'], .3)
        self.assertEqual(r['runsAtLeast100ms'][0]['maximumReferencePaintGapUs'], 100000)

    def test_changed_pixels_under_same_cache_key_fails(self):
        frames, traces, paints = sample()
        frames[1]['fixtureId'] = 99
        self.assertFalse(audit.correlate(frames, traces, paints, 30)['checks']['sameSourceKeyHasSameDecodedId'])

    def test_session_epoch_allows_new_pixels(self):
        frames, traces, paints = sample()
        for t in traces[2:]:
            t['sourceSequence'], t['sourceEpoch'] = 1, 2
        self.assertTrue(audit.correlate(frames, traces, paints, 30)['diagnosticChecksPassed'])

    def test_missing_and_duplicate_trace_fail(self):
        frames, traces, paints = sample()
        self.assertFalse(audit.correlate(frames, traces[1:], paints, 30)['checks']['allCommittedFramesHaveTrace'])
        self.assertFalse(audit.correlate(frames, traces+[copy.copy(traces[0])], paints, 30)['checks']['uniqueTraceFrameIndices'])

    def test_cache_readback_and_phase_reversal_fail(self):
        frames, traces, paints = sample()
        traces[1]['readbackUs'] = 1
        traces[1]['readEndQpc100ns'] = 0
        r = audit.correlate(frames, traces, paints, 30)
        self.assertFalse(r['checks']['cachedFramesHaveZeroReadback'])
        self.assertFalse(r['checks']['qpcPhaseOrder'])

    def test_legacy_trace_does_not_claim_cache_or_qpc_evidence(self):
        frames, traces, paints = sample()
        traces = [{k: v for k, v in t.items() if k not in ('pixelsCached', 'sourceEpoch') and not k.endswith('Qpc100ns')} for t in traces]
        r = audit.correlate(frames, traces, paints, 30)
        self.assertTrue(r['diagnosticChecksPassed'])
        self.assertFalse(r['pixelCacheEvidenceAvailable'])
        self.assertFalse(r['qpcBreakdownAvailable'])
        self.assertIsNone(r['committedPixelCacheHits'])


if __name__ == '__main__':
    unittest.main()
