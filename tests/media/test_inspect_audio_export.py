"""Regression tests for the independent signal oracle, using controlled PCM.

Actual Windows AAC files are audited separately by Inspect-AudioExport.py.
"""
import importlib.util, math, unittest
from pathlib import Path
spec = importlib.util.spec_from_file_location('inspect_audio_export', Path(__file__).resolve().parents[2] / 'scripts/Inspect-AudioExport.py')
audit = importlib.util.module_from_spec(spec); spec.loader.exec_module(audit)


def tones(microphone=True, system=True, removed=False):
    channels = [[], []]
    for n in range(144000):
        time = n / 48000; frequency = 440 if time < 1 else 880 if removed else 660
        for channel in range(2):
            f = frequency * (1 if channel == 0 else 1.25)
            channels[channel].append((.5 * math.sin(2 * math.pi * f * time) * microphone + .25 * math.sin(4 * math.pi * f * time) * system) * 6000 / 32768)
    return channels


class AudioExportOracleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls): cls.reference = tones()
    def test_controlled_mix_and_explicit_microphone_mute(self):
        self.assertTrue(all(c['passed'] for c in audit.signal_checks(self.reference)))
        self.assertTrue(all(c['passed'] for c in audit.signal_checks(tones(microphone=False), True)))
    def test_removed_audio_and_unexpected_microphone_are_detected(self):
        self.assertFalse(all(c['passed'] for c in audit.signal_checks(tones(removed=True))))
        self.assertFalse(all(c['passed'] for c in audit.signal_checks(self.reference, True)))
    def test_encoder_delay_is_measured_and_85ms_fails(self):
        for delay, expected in [(0, True), (480, True), (4080, False)]:
            shifted = [[0.0] * delay + channel for channel in self.reference]
            result = audit.compare_pcm(shifted, self.reference[0])
            self.assertEqual(expected, result['passed']); self.assertEqual(delay, result['delay_samples'])
    def test_truncated_audio_returns_failed_evidence(self):
        self.assertFalse(audit.compare_pcm([[0.0], [0.0]], [0.0])['passed'])
        self.assertFalse(all(c['passed'] for c in audit.signal_checks([[0.0], [0.0]])))


if __name__ == '__main__': unittest.main()
