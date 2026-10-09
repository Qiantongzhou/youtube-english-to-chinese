import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
from speech_regions import partition, speech_seconds
from speech_rate import budget
from sentence_alignment import sentences, schedule


class SpeechRegionTests(unittest.TestCase):
    def test_partition_preserves_head_middle_tail_and_short_pauses(self):
        spans = [{"start": 1, "end": 2}, {"start": 2.8, "end": 4}, {"start": 10, "end": 12}]
        layout = partition(spans, 15)
        self.assertEqual(len(layout["blocks"]), 2)
        self.assertEqual(len(layout["gaps"]), 3)
        all_parts = sorted(layout["blocks"] + layout["gaps"], key=lambda r: r["start"])
        self.assertEqual(all_parts[0]["start"], 0)
        self.assertEqual(all_parts[-1]["end"], 15)
        for left, right in zip(all_parts, all_parts[1:]):
            self.assertEqual(left["end"], right["start"])
        self.assertAlmostEqual(speech_seconds(1.5, 11, spans), 2.7)

    def test_no_speech_returns_whole_clip_as_gap(self):
        self.assertEqual(partition([], 15)["gaps"], [{"id": 1, "start": 0.0, "end": 15}])

    def test_exact_threshold_splits(self):
        self.assertEqual(len(partition([{"start": 0, "end": 1}, {"start": 3, "end": 4}], 5)["blocks"]), 2)

    def test_word_budget_excludes_silence(self):
        self.assertEqual(budget({"start": 161, "end": 190, "speech_duration": 6}, 4.5)["target"], 27)

    def test_sentences_cannot_merge_across_gap_without_punctuation(self):
        rows = [{"id": 1, "start": 1, "end": 3, "zh": "第一段", "speech_block": 1, "block_start": 1, "block_end": 3, "speech_duration": 1.5},
                {"id": 2, "start": 10, "end": 12, "zh": "第二段。", "speech_block": 2, "block_start": 10, "block_end": 12, "speech_duration": 1.7}]
        grouped = sentences(rows)
        self.assertEqual(len(grouped), 2)
        self.assertEqual(grouped[0]["speech_duration"], 1.5)
        placement = schedule(grouped, [15, 1], 4)
        self.assertEqual([s["start"] for s in placement], [1, 10])
        self.assertGreater(placement[0]["gap_overflow"], 0)
        self.assertFalse(placement[0]["within_tolerance"])
        self.assertTrue(placement[1]["within_tolerance"])
