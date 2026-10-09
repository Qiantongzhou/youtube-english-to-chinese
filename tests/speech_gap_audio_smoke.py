"""Real FFmpeg fitting and composition with synthetic speech; no model-quality claim."""
import sys
from pathlib import Path
import numpy as np
import soundfile as sf
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
from core import ROOT, run, tool, write_json, probe
from dubbing import render_dub, protect_speech_gaps
from worker import compose
from speech_regions import background_track, export_parts

folder = ROOT / "artifacts" / "speech-gap-audio-verified"
folder.mkdir(exist_ok=True)
rows = [
    {"id": 1, "start": 1, "end": 2, "zh": "第一句。", "speech_block": 1, "block_start": 1, "block_end": 4},
    {"id": 2, "start": 2, "end": 4, "zh": "第二句。", "speech_block": 1, "block_start": 1, "block_end": 4},
    {"id": 3, "start": 10, "end": 12, "zh": "第三句。", "speech_block": 2, "block_start": 10, "block_end": 12}]
raws = []
for i, duration in enumerate([6, 4, 3]):
    path = folder / f"raw-{i}.wav"
    sf.write(str(path), .1*np.sin(np.arange(round(duration*24000))*2*np.pi*330/24000), 24000)
    raws.append(path)
_, timing = render_dub(rows, raws, folder, 15, alignment_tolerance=4)
count = protect_speech_gaps(rows, timing, folder, 15)
assert count == 1, count
assert timing[0]["block_speedup"] == timing[1]["block_speedup"]
assert timing[0]["start"] == 1 and timing[2]["start"] == 10
assert timing[0]["end"] <= timing[1]["start"] and timing[1]["end"] <= 4 and timing[2]["end"] <= 12
audio, rate = sf.read(str(folder / "dub.wav"))
assert len(audio) == 15*rate
for start, end in [(0, 1), (4, 10), (12, 15)]:
    assert np.max(np.abs(audio[start*rate:end*rate])) == 0
for entry in timing:
    clip, _ = sf.read(entry["file"])
    assert np.max(np.abs(clip[-1200:])) > .01, "Voice tail disappeared"
write_json(folder / "segments.json", rows)
layout = {"blocks": [{"id": 1, "start": 1, "end": 4}, {"id": 2, "start": 10, "end": 12}],
    "gaps": [{"id": i+1, "start": s, "end": e} for i, (s, e) in enumerate([(0, 1), (4, 10), (12, 15)])]}
write_json(folder / "speech_regions.json", layout)
original = folder / "original.wav"
stem = folder / "background.wav"
sf.write(str(original), np.full(15*24000, .1), 24000, subtype="FLOAT")
sf.write(str(stem), np.full((15*32000, 2), .2), 32000, subtype="FLOAT")
export_parts(original, folder, layout)
assert sum(sf.info(str(f)).frames for label in ["人声分段", "无语音片段"] for f in (folder/label).glob("*.wav")) == 15*24000
bg = background_track({"audio": str(original), "background": str(stem)}, folder)
mixed, _ = sf.read(bg, dtype="float32")
assert np.all(mixed[4*24000:10*24000] == np.float32(.1))
resampled, _ = sf.read(str(folder / "background-resampled.wav"), dtype="float32")
assert np.allclose(mixed[24000:4*24000], resampled[24000:4*24000]*.65)
video = folder / "source.mp4"
run([tool("ffmpeg"), "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=25:d=15", "-an", "-c:v", "libx264", video])
p = {"duration": 15, "dub_duration": 15, "dub_audio": str(folder / "dub.wav"), "video": str(video),
     "audio": str(original), "background": str(stem), "speech_layout_version": 1,
     "active_sentence_alignment": True, "settings": {"dub": True, "burn_subtitles": True}}
compose(p, folder)
assert abs(float(probe(p["result"])["format"]["duration"])-15) < .1
write_json(folder / "validation.json", {"OriginalDurationSeconds": 15, "GapSamplesExactlyZero": True,
    "BlockStartsAligned": True, "UniformBlockSpeed": timing[0]["block_speedup"], "CompleteVoiceTails": True,
    "OriginalGapAudioPreservedBeforeEncoding": True, "IndependentAudioPartsCoverWholeVideo": True})
print("Speech gap protection and original video duration: PASS")
