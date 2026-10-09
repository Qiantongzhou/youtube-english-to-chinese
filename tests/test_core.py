import json
import math
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
from core import youtube_url, timestamp, validate_segments, ass_text, tempo_filter, subtitles, write_json, read_json
import engine


class CoreTests(unittest.TestCase):
    def test_url_canonicalization_discards_tracking_and_playlist(self):
        for url in ["https://youtu.be/jNQXAC9IVRw?si=abc", "https://www.youtube.com/watch?v=jNQXAC9IVRw&list=foo", "https://m.youtube.com/shorts/jNQXAC9IVRw"]:
            self.assertEqual(youtube_url(url), "https://www.youtube.com/watch?v=jNQXAC9IVRw")

    def test_invalid_hosts_and_nonvideo_urls_rejected(self):
        for url in ["https://youtube.com.evil.test/watch?v=jNQXAC9IVRw", "file:///c:/video.mp4", "https://youtube.com/playlist?list=abc", "--exec calc", "https://user@youtube.com/watch?v=jNQXAC9IVRw", "https://youtu.be/xyz"]:
            with self.subTest(url=url), self.assertRaises(ValueError): youtube_url(url)

    def test_timestamp_rounding_carries_into_next_minute(self):
        self.assertEqual(timestamp(59.9999), "00:01:00,000")
        self.assertEqual(timestamp(3599.9999, True), "1:00:00.00")

    def test_timeline_rejects_overlap_nonfinite_empty_and_overrun(self):
        good = [{"start": 0, "end": 1, "zh": "你好"}]
        validate_segments(good, 1)
        for bad in [[], [{"start": -1, "end": 2, "zh": "好"}], [{"start": 0, "end": math.inf, "zh": "好"}],
                    [{"start": 0, "end": 1, "zh": ""}], [{"start": 0, "end": 2, "zh": "好"}],
                    good + [{"start": 0.5, "end": 1, "zh": "重叠"}]]:
            with self.subTest(bad=bad), self.assertRaises(ValueError): validate_segments(bad, 1)

    def test_ass_override_injection_is_literal(self):
        escaped = ass_text(r"{\p1}m 0 0 l 100 100")
        self.assertNotIn("{", escaped)
        self.assertNotIn(r"\p1", escaped)

    def test_tempo_chain_preserves_requested_ratio(self):
        for ratio in [0.1, 0.8, 1.0, 1.3, 5.0]:
            factors = [float(p.split("=")[1]) for p in tempo_filter(ratio).split(",")]
            self.assertTrue(all(0.5 <= f <= 2 for f in factors))
            self.assertAlmostEqual(math.prod(factors), ratio, places=6)

    def test_unicode_subtitle_files_and_atomic_project(self):
        with tempfile.TemporaryDirectory(prefix="中文 空格") as folder:
            subtitles([{"start": 1.23, "end": 2.34, "zh": "中文字幕"}], folder)
            srt = (Path(folder) / "中文配音.srt").read_text(encoding="utf-8-sig")
            self.assertIn("00:00:01,230 --> 00:00:02,340", srt)
            file = Path(folder) / "project.json"
            write_json(file, {"title": "中文"})
            self.assertEqual(read_json(file)["title"], "中文")
            self.assertFalse(file.with_suffix(".json.tmp").exists())

    def test_resume_skips_completed_stage_and_persists_new_success(self):
        with tempfile.TemporaryDirectory() as folder:
            file = Path(folder) / "project.json"
            write_json(file, {"version": 1, "settings": {}, "completed": ["download", "extract"], "status": "transcribe"})
            with patch.object(engine, "worker") as worker, patch.object(engine, "emit"):
                engine.execute("resume", file)
            self.assertEqual([call.args[0] for call in worker.call_args_list], ["transcribe", "translate"])
            self.assertEqual(read_json(file)["status"], "review")

    def test_failed_stage_is_not_marked_complete(self):
        with tempfile.TemporaryDirectory() as folder:
            file = Path(folder) / "project.json"
            write_json(file, {"settings": {}, "completed": ["download", "extract"]})
            with patch.object(engine, "worker", side_effect=RuntimeError("GPU failed")), self.assertRaises(RuntimeError):
                engine.execute("resume", file)
            self.assertNotIn("transcribe", read_json(file)["completed"])

    def test_worker_error_keeps_traceback_out_of_user_message(self):
        def failed_run(args, on_line):
            on_line("Traceback: detailed diagnostic")
            on_line('{"type":"error","message":"Cookie database is locked"}')
            raise RuntimeError("python.exe exit 1\nTraceback: detailed diagnostic")
        with patch.object(engine, "run", side_effect=failed_run), patch.object(engine, "emit") as emit:
            with self.assertRaises(engine.WorkerFailure) as failure:
                engine.worker("download", "project.json")
        self.assertEqual(str(failure.exception), "Cookie database is locked")
        emit.assert_called_once_with("log", message="Traceback: detailed diagnostic")

    def test_login_check_uses_selected_cookie_file(self):
        with tempfile.TemporaryDirectory() as folder:
            cookies = Path(folder) / "cookies.txt"
            cookies.write_text("# Netscape HTTP Cookie File\n", encoding="utf-8")
            request = Path(folder) / "request.json"
            write_json(request, {"source": "https://youtu.be/jNQXAC9IVRw", "cookie_source": "file", "cookies": str(cookies)})
            with patch.object(engine, "run") as run, patch.object(engine, "tool", side_effect=lambda name: name), patch.object(engine, "emit") as emit:
                engine.check_login(request)
            args = run.call_args.args[0]
            self.assertEqual(args[args.index("--cookies") + 1], str(cookies))
            self.assertNotIn("--cookies-from-browser", args)
            self.assertEqual(emit.call_args.args[0], "login_checked")


if __name__ == "__main__": unittest.main()
