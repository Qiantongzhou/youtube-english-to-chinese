import json
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
from speech_rate import units, budget, outside, target_rate
from dubbing import placement
from worker import translate_batch


class SpeechRateTests(unittest.TestCase):
    def test_spoken_units_exclude_punctuation_and_group_words(self):
        self.assertEqual(units("你好，VideoCN 2026 年！"), 5)

    def test_budget_and_outlier_detection(self):
        segment = {"start": 1, "end": 5, "zh": "太短"}
        self.assertEqual(budget(segment, 4), {"target": 16, "min": 12, "max": 20})
        self.assertTrue(outside(segment, 4))
        self.assertFalse(outside({**segment, "zh": "字" * 16}, 4))

    def test_setting_bounds(self):
        self.assertEqual(target_rate({}), 4)
        for value in [0, 9, float("nan")]:
            with self.assertRaises(ValueError): target_rate({"chars_per_second": value})

    def test_equal_text_uses_equal_rate_regardless_of_subtitle_window(self):
        short = {"start": 0, "end": 2, "zh": "字" * 16}
        long = {**short, "end": 8}
        self.assertEqual(placement(short, 5, 0, 4)[1], 1.25)
        self.assertEqual(placement(short, 5, 0, 4)[1], placement(long, 5, 0, 4)[1])
        self.assertEqual(placement(short, 3, 0, 4)[1], .75)

    def test_translation_request_contains_rate_and_per_sentence_budget(self):
        session = Mock()
        response = session.post.return_value
        response.json.return_value = {"message": {"content": json.dumps({"translations": [{"id": 1, "zh": "测试文字"}]})}}
        result = translate_batch(session, "http://local", "model", [{"id": 1, "start": 0, "end": 2, "en": "Test", "zh": "旧文案"}], "", "", True, rate=5)
        messages = session.post.call_args.kwargs["json"]["messages"]
        self.assertIn("每秒 5 字", messages[0]["content"])
        segment = json.loads(messages[1]["content"])["segments"][0]
        self.assertEqual(segment["length_budget"]["target"], 10)
        self.assertEqual(segment["draft"], "旧文案")
        self.assertEqual(result[1], "测试文字")
