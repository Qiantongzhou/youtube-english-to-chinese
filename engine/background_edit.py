"""Range-based background edits, keeping the Chinese voice track untouched."""
import math
from pathlib import Path
from core import read_json, run, tool


def load(project, folder):
    path = Path(folder) / "background_edit.json"
    if not path.is_file(): return {"version": 1, "enabled": project["settings"].get("background", True), "ranges": []}
    data = read_json(path)
    if data.get("version") != 1 or not isinstance(data.get("ranges"), list):
        raise ValueError("背景音轨编辑文件格式无效。")
    previous = 0
    for item in sorted(data["ranges"], key=lambda r: float(r["start"])):
        values = [float(item[k]) for k in ("start", "end", "gain", "fade")]
        start, end, gain, fade = values
        if not all(math.isfinite(v) for v in values) or start < previous - .0001 or end <= start or end > project["duration"] + .001:
            raise ValueError("背景音调整范围无效、互相重叠或超出原视频。")
        if not 0 <= gain <= 1 or not 0 <= fade <= .5 or item["mode"] not in {"auto", "separated"}:
            raise ValueError("背景音量或处理方式无效。")
        previous = end
    return data


def enabled(project, folder):
    return load(project, folder).get("enabled", True) is True


def render(project, folder):
    import numpy as np
    import soundfile as sf
    folder = Path(folder)
    edits = load(project, folder)
    background = Path(project.get("background") or "")
    if not background.is_file(): background = folder / "stems" / "htdemucs" / "original" / "no_vocals.wav"
    if not background.is_file():
        raise ValueError("项目缺少分离背景，请先开启保留背景声并生成中文视频。")
    info = sf.info(str(background))
    original = Path(project.get("audio") or "")
    layout = folder / "speech_regions.json"
    gaps = read_json(layout).get("gaps", []) if layout.is_file() and project.get("speech_layout_version") == 1 else []
    if gaps and not original.is_file(): raise ValueError("缺少 original.wav，无法恢复空挡背景声音。")
    if gaps:
        raw_info = sf.info(str(original))
        if (raw_info.samplerate, raw_info.channels) != (info.samplerate, info.channels):
            converted = folder / "background-original-resampled.wav"
            run([tool("ffmpeg"), "-nostdin", "-v", "error", "-y", "-i", original,
                 "-ar", str(info.samplerate), "-ac", str(info.channels), "-c:a", "pcm_f32le", converted])
            original = converted
    target = folder / "background-edited.wav"
    partial = target.with_suffix(".partial.wav")
    # One-second chunks bound memory use even with many edited intervals.
    total = math.ceil(float(project["duration"]) * info.samplerate)
    raw_file = sf.SoundFile(str(original)) if gaps else None
    try:
        with sf.SoundFile(str(background)) as stem, sf.SoundFile(str(partial), "w", samplerate=info.samplerate,
                channels=info.channels, subtype="FLOAT") as out:
            offset = 0
            while offset < total:
                count = min(info.samplerate, total - offset)
                times = (offset + np.arange(count)) / info.samplerate
                raw_weight = np.zeros(count, dtype=np.float32)
                for gap in gaps: raw_weight[(times >= gap["start"]) & (times < gap["end"])] = 1
                stem_weight = (1 - raw_weight) * .65
                for item in edits["ranges"]:
                    start, end = item["start"], item["end"]
                    if end <= times[0] or start > times[-1]: continue
                    mask = (times >= start) & (times < end)
                    fade = min(item["fade"], (end - start) / 2)
                    blend = np.minimum(np.minimum((times - start) / fade, (end - times) / fade), 1).clip(0, 1) if fade > 0 else mask.astype(np.float32)
                    gain = item["gain"]
                    wanted_raw = raw_weight * gain if item["mode"] == "auto" else np.zeros(count)
                    wanted_stem = stem_weight * gain if item["mode"] == "auto" else np.full(count, .65 * gain)
                    raw_weight = raw_weight * (1 - blend) + wanted_raw * blend
                    stem_weight = stem_weight * (1 - blend) + wanted_stem * blend
                data = np.zeros((count, info.channels), dtype=np.float32)
                audio = stem.read(count, dtype="float32", always_2d=True)
                data[:len(audio)] += audio * stem_weight[:len(audio), None]
                if raw_file:
                    audio = raw_file.read(count, dtype="float32", always_2d=True)
                    data[:len(audio)] += audio * raw_weight[:len(audio), None]
                out.write(data); offset += count
    finally:
        if raw_file: raw_file.close()
    partial.replace(target)
    return str(target)
