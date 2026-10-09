"""Measure FFmpeg rate calibration independently of subtitle-window duration."""
from pathlib import Path
import sys
import numpy as np
import soundfile as sf
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
from core import ROOT, write_json
from dubbing import render_dub

folder = ROOT / "artifacts" / "speech-rate-audio-verified"
folder.mkdir(exist_ok=True)
raws = []
for index, duration in enumerate([3, 5]):
    raw = folder / f"synthetic-{index}.wav"
    sf.write(str(raw), .1 * np.sin(np.arange(duration * 24000) * 2 * np.pi * 330 / 24000), 24000)
    raws.append(raw)
segments = [{"id": 1, "start": 0, "end": 2, "zh": "字" * 16}, {"id": 2, "start": 2, "end": 10, "zh": "字" * 16}]
_, timeline = render_dub(segments, raws, folder, 10, rate=4)
rates = [entry["chars_per_second"] for entry in timeline]
assert all(abs(rate - 4) < .15 for rate in rates), rates
assert abs(rates[0] - rates[1]) < .15
write_json(folder / "validation.json", {"TargetRate": 4, "MeasuredRates": rates, "DifferentWindowLengthsDoNotChangeRate": True})
print("Measured speech rate normalization: PASS", rates)
