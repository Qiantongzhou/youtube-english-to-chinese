"""Real FFmpeg integration: Unicode paths, ASS burn-in, mixing and output duration."""
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
from core import tool, run, probe, write_json
from worker import extract, compose

folder = ROOT / "artifacts" / "媒体 验证"
folder.mkdir(parents=True, exist_ok=True)
source = folder / "测试 原片.mp4"
run([tool("ffmpeg"), "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=0x173c35:s=1280x720:r=25:d=6",
     "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=44100:duration=6", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source])
segments = [{"id": 1, "start": 0.5, "end": 2.5, "en": "A test.", "zh": "这是中文配音与字幕测试。"},
            {"id": 2, "start": 3.0, "end": 5.5, "en": "Unicode paths.", "zh": "支持中文路径，也支持空格。"}]
write_json(folder / "segments.json", segments)
p = {"version": 1, "settings": {"dub": False, "burn_subtitles": True}, "duration": 6.0, "video": str(source)}
extract(p, folder)
compose(p, folder)
original_output = folder / "original_audio_subtitles.mp4"
(folder / "中文配音.mp4").replace(original_output)
run([tool("ffmpeg"), "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=24000:duration=6", folder / "dub.wav"])
p["settings"]["dub"] = True
p["dub_audio"] = str(folder / "dub.wav")
p["background"] = p["audio"]
compose(p, folder)
result = probe(p["result"])
assert {s["codec_type"] for s in result["streams"]} >= {"video", "audio"}
assert abs(float(result["format"]["duration"]) - 6.0) < 0.1
run([tool("ffmpeg"), "-nostdin", "-v", "error", "-y", "-ss", "1.2", "-i", p["result"], "-frames:v", "1", folder / "subtitle-frame.png"])
write_json(folder / "validation.json", {"ok": True, "duration": result["format"]["duration"], "streams": [{"type": s["codec_type"], "codec": s["codec_name"]} for s in result["streams"]], "note": "Synthetic media verifies composition, not AI accuracy or voice quality."})
print("MEDIA_SMOKE_OK " + str(folder))
