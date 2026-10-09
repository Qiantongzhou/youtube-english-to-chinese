import math
from pathlib import Path
import sys
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
from dubbing import placement
from core import validate_segments


class DubbingTests(unittest.TestCase):
    def test_long_clip_caps_speed_and_next_clip_moves(self):
        segment = {"start": 0, "end": 4}
        start, speed = placement(segment, 6.8, 0)
        self.assertEqual((start, speed), (0, 1.5))
        self.assertAlmostEqual(placement({"start": 4, "end": 5}, 1, 6.8 / 1.5)[0], 6.8 / 1.5)

    def test_manual_position_is_independent_and_can_overlap(self):
        self.assertEqual(placement({"start": 5, "end": 6, "audio_start": 2}, 1, 9), (2, 1))
        self.assertEqual(placement({"start": 5, "end": 6, "audio_start": 0}, 1, 9), (0, 1))

    def test_invalid_manual_positions_fail_validation(self):
        for value in [-1, math.inf, math.nan]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                validate_segments([{"start": 0, "end": 1, "zh": "示例", "audio_start": value}], 2)
