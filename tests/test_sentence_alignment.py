import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
from sentence_alignment import sentences, schedule, tolerance
from core import write_json, read_json
import engine


class SentenceAlignmentTests(unittest.TestCase):
    def test_cross_row_merge_and_inside_row_split_preserve_text_and_sources(self):
        rows = [{"id": 10, "start": 0, "end": 1, "zh": "这是第一", "en": "This is"},
                {"id": 11, "start": 1, "end": 4, "zh": "句话。第二句话。", "en": "the first sentence. Second sentence."}]
        result = sentences(rows)
        self.assertEqual([s["zh"] for s in result], ["这是第一句话。", "第二句话。"])
        self.assertEqual(result[0]["source_ids"], [10, 11])
        self.assertEqual((result[0]["end"], result[1]["start"]), (2, 2))
        self.assertEqual(result[-1]["end"], 4)

    def test_quotes_decimal_tail_and_manual_boundary(self):
        rows = [{"id": 1, "start": 0, "end": 4, "zh": "版本1.2，说：“好。”然后继续", "en": ""},
                {"id": 2, "start": 4, "end": 6, "zh": "下一句。", "en": "", "audio_start": 5}]
        result = sentences(rows)
        self.assertEqual([s["zh"] for s in result], ['版本1.2，说：“好。”', '然后继续', '下一句。'])
        self.assertEqual(result[-1]["audio_start"], 5)

    def test_lookahead_moves_previous_sentence_earlier_to_fit(self):
        rows = [{"start": 3, "end": 5}, {"start": 5, "end": 7}]
        result = schedule(rows, [4, 4], 2)
        self.assertTrue(all(e["within_tolerance"] for e in result))
        self.assertLess(result[0]["start"], 3)
        self.assertLessEqual(result[0]["end"], result[1]["start"])

    def test_impossible_timing_retains_full_audio_and_reports_drift(self):
        result = schedule([{"start": 0, "end": 2}, {"start": 2, "end": 4}], [8, 8], 2)
        self.assertTrue(any(not e["within_tolerance"] for e in result))
        self.assertAlmostEqual(result[0]["end"]-result[0]["start"], 8)
        self.assertGreaterEqual(result[1]["start"], result[0]["end"])

    def test_manual_positions_remain_exact(self):
        result = schedule([{"start": 0, "end": 3}, {"start": 3, "end": 6, "audio_start": 1}], [4, 4], 2)
        self.assertEqual(result[1]["start"], 1)
        self.assertTrue(result[1]["manual"])
        with self.assertRaises(ValueError): tolerance({"alignment_tolerance": 5})

    def test_four_second_tolerance_accepts_natural_timing_without_rewriting(self):
        rows = [{"start": 0, "end": 4}, {"start": 4, "end": 6}]
        self.assertEqual(tolerance({}), 4)
        self.assertTrue(all(s["within_tolerance"] for s in schedule(rows, [7.739, 1.75], 4)))
        self.assertTrue(any(not s["within_tolerance"] for s in schedule(rows, [7.739, 1.75], 2)))

    def test_three_round_cap_restores_best_complete_round(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory); path = folder / "project.json"
            write_json(path, {"settings": {"dub": True}, "completed": ["translate"]})
            calls = []
            def fake(stage, project_path):
                calls.append(stage); p = read_json(project_path)
                if stage == "sentence_prepare": p["alignment_history"] = []
                if stage == "tts":
                    r = p["alignment_round"]
                    p.update(dub_audio=str(folder / "dub.wav"), dub_duration=r, timing_adjustments=1, alignment_unresolved=1)
                    p["alignment_history"].append({"round": r, "max_boundary_error": {1: 5, 2: 3, 3: 4}[r]})
                    (folder / "dub.wav").write_bytes(f"round{r}".encode())
                    write_json(folder / "dub_timing.json", [{"id": r}])
                    write_json(folder / "sentence_segments.json", [{"id": r}])
                    (folder / "配音时间表.csv").write_text(str(r))
                if stage == "align_text": p["alignment_text_changed"] = True
                if stage == "compose": self.assertEqual((folder / "dub.wav").read_bytes(), b"round2")
                write_json(project_path, p)
            with patch.object(engine, "worker", side_effect=fake), patch.object(engine, "emit"):
                engine.execute("render", path)
            self.assertEqual(calls.count("tts"), 3)
            self.assertEqual(calls.count("align_text"), 2)
            self.assertEqual(read_json(path)["alignment_selected_round"], 2)
            self.assertEqual(read_json(path)["status"], "done")
