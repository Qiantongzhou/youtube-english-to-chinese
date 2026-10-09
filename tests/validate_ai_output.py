"""Inspect the actual AI output and independently transcribe its Chinese speech."""
from pathlib import Path
import sys
import json

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
from core import read_json, write_json, tool, run, probe, process_env
from worker import torch_setup
import os
os.environ.update(process_env())
import soundfile as sf
import numpy as np

project_path = Path(sys.argv[1]).resolve()
p = read_json(project_path)
folder = project_path.parent
assert p["status"] == "done"
assert set(p["completed"]) == {"download", "extract", "transcribe", "translate", "separate", "tts", "compose"}
result = probe(p["result"])
assert abs(float(result["format"]["duration"]) - p["duration"]) < 0.1
audio, rate = sf.read(p["dub_audio"], dtype="float32")
assert abs(len(audio) / rate - p["duration"]) < 0.01
assert np.max(np.abs(audio)) > 0.1
assert Path(p["background"]).is_file()
run([tool("ffmpeg"), "-nostdin", "-v", "error", "-y", "-ss", "1.5", "-i", p["result"], "-frames:v", "1", folder / "result-frame.png"])
torch_setup()
from faster_whisper import WhisperModel
from faster_whisper.utils import download_model
cached = download_model("large-v3", cache_dir=str(ROOT / "models" / "whisper"), local_files_only=True)
model = WhisperModel(cached, device="cuda", compute_type="int8_float16")
chunks, _ = model.transcribe(p["dub_audio"], language="zh", beam_size=5, vad_filter=True)
heard = [{"start": s.start, "end": s.end, "text": s.text} for s in chunks]
assert heard, "Generated speech was not recognized"
write_json(folder / "validation.json", {"ok": True, "duration": result["format"]["duration"],
    "dub_duration": len(audio) / rate, "peak": float(np.max(np.abs(audio))), "recognition_of_generated_voice": heard,
    "note": "ASR is an additional intelligibility check, not a subjective human listening test."})
print(json.dumps(heard, ensure_ascii=False))
