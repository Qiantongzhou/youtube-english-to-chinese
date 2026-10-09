"""Actual FFmpeg export using synthetic long audio, not an AI quality test."""
import hashlib
import json
from pathlib import Path
import sys
import numpy as np
import soundfile as sf
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
from core import ROOT, run, tool, write_json, probe
from worker import tts, compose
from tts_parallel import style_signature

folder = ROOT / "artifacts" / "dubbing-overflow-verified"
folder.mkdir(parents=True, exist_ok=True)
segments = [{"id": 1, "start": 0, "end": 4, "zh": "合成长句", "en": "synthetic"},
            {"id": 2, "start": 4, "end": 5, "zh": "合成第二句", "en": "synthetic"}]
write_json(folder / "segments.json", segments)
cache = folder / "voice_cache"; cache.mkdir(exist_ok=True)
for segment, duration in zip(segments, [6.8, 3]):
    signature = style_signature("Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice", "Serena", segment["zh"])
    sf.write(str(cache / (signature + ".wav")), .1 * np.sin(np.arange(round(duration * 24000)) * 2 * np.pi * 330 / 24000), 24000)
video = folder / "source.mp4"
run([tool("ffmpeg"), "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=25:d=5", "-an", "-c:v", "libx264", str(video)])
p = {"duration": 5, "video": str(video), "settings": {"mode": "quality", "voice": "Serena", "dub": True, "burn_subtitles": True}}
tts(p, folder)
timeline = json.loads((folder / "dub_timing.json").read_text(encoding="utf-8"))
assert timeline[0]["end"] > 4.4 and timeline[1]["start"] == timeline[0]["end"]
assert p["dub_duration"] > 6.3
compose(p, folder)
data = probe(p["result"])
assert abs(float(data["format"]["duration"]) - p["dub_duration"]) < .2
audio, rate = sf.read(str(folder / "dub.wav"))
assert np.max(np.abs(audio[-2400:])) > .01, "Audio tail was cut"
# Explicit overlapping positions should mix at requested starts, not append.
segments[1]["audio_start"] = 1
write_json(folder / "segments.json", segments)
tts(p, folder)
manual = json.loads((folder / "dub_timing.json").read_text(encoding="utf-8"))
assert manual[1]["start"] == 1 and "重叠" in manual[1]["note"]
write_json(folder / "validation.json", {"LongClipPreserved": True, "FollowingClipShifted": True, "VideoExtendedSeconds": float(data["format"]["duration"]), "ManualPositionApplied": True, "CachedTtsReusedWithoutModel": True})
print("Dubbing overflow and manual placement: PASS")
