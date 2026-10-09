"""Keep speech islands and non-speech intervals on the original media clock."""
from pathlib import Path
from core import write_json, read_json, run, tool

SILENCE_AFTER_SPEECH_SECONDS = 2.0


def partition(spans, duration, gap=SILENCE_AFTER_SPEECH_SECONDS, padding=.12):
    groups = []
    last_speech_end = None
    for span in sorted(spans, key=lambda s: float(s["start"])):
        start, end = max(0, float(span["start"])), min(duration, float(span["end"]))
        if end <= start: continue
        # 从实际语音结束计时；期间再次说话就重置，连续无语音满 2 秒才分段。
        # 不使用字幕时间或后面添加的首尾缓冲计算这 2 秒。
        silence_seconds = start - last_speech_end if last_speech_end is not None else 0
        if last_speech_end is None or silence_seconds >= gap - 1e-9:
            groups.append({"start": start, "end": end, "speech_spans": [{"start": start, "end": end}]})
        else:
            groups[-1]["end"] = max(groups[-1]["end"], end)
            groups[-1]["speech_spans"].append({"start": start, "end": end})
        last_speech_end = max(last_speech_end if last_speech_end is not None else end, end)
    blocks, gaps, cursor = [], [], 0.0
    for index, group in enumerate(groups):
        start, end = max(0, group["start"]-padding), min(duration, group["end"]+padding)
        if start > cursor: gaps.append({"id": len(gaps)+1, "start": cursor, "end": start})
        blocks.append({**group, "id": index+1, "start": start, "end": end})
        cursor = end
    if cursor < duration: gaps.append({"id": len(gaps)+1, "start": cursor, "end": duration})
    return {"version": 1, "duration": duration, "gap_threshold": gap, "blocks": blocks, "gaps": gaps}


def speech_seconds(start, end, spans):
    return sum(max(0, min(end, span["end"]) - max(start, span["start"])) for span in spans)


def analyze(project, folder):
    from faster_whisper.audio import decode_audio
    from faster_whisper.vad import get_speech_timestamps, VadOptions
    source = Path(project["audio"])
    background = project.get("background")
    vocals = Path(background).with_name("vocals.wav") if background else folder / "stems" / "htdemucs" / "original" / "vocals.wav"
    detected_from = vocals if vocals.is_file() else source
    audio = decode_audio(str(detected_from), sampling_rate=16000)
    spans = get_speech_timestamps(audio, VadOptions(min_speech_duration_ms=180, min_silence_duration_ms=500, speech_pad_ms=0))
    spans = [{"start": span["start"]/16000, "end": span["end"]/16000} for span in spans]
    layout = partition(spans, project["duration"])
    layout["detected_from"] = str(detected_from)
    write_json(folder / "speech_regions.json", layout)
    export_parts(source, folder, layout)
    return layout


def export_parts(source, folder, layout):
    import csv
    import soundfile as sf
    entries = []
    with sf.SoundFile(str(source)) as original:
        for key, label in [("blocks", "人声分段"), ("gaps", "无语音片段")]:
            directory = folder / label; directory.mkdir(exist_ok=True)
            for region in layout[key]:
                target = directory / f"{region['id']:04d}.wav"
                first = round(region["start"] * original.samplerate)
                last = min(len(original), round(region["end"] * original.samplerate))
                original.seek(first)
                with sf.SoundFile(str(target), "w", samplerate=original.samplerate, channels=original.channels, subtype="PCM_16") as output:
                    remaining = last-first
                    while remaining > 0:
                        part = original.read(min(remaining, original.samplerate*30), dtype="float32")
                        if len(part) == 0: break
                        output.write(part); remaining -= len(part)
                entries.append([label, region["id"], region["start"], region["end"], str(target.resolve())])
    with (folder / "人声与空挡时间表.csv").open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.writer(handle); writer.writerow(["类型", "编号", "原片开始秒", "原片结束秒", "独立音频"])
        writer.writerows(sorted(entries, key=lambda e: e[2]))


def background_track(project, folder):
    """Keep original non-speech audio; use the quieter separated stem during speech."""
    import soundfile as sf
    import numpy as np
    source, background = Path(project["audio"]), Path(project["background"])
    info, bg_info = sf.info(str(source)), sf.info(str(background))
    if (info.samplerate, info.channels) != (bg_info.samplerate, bg_info.channels):
        converted = folder / "background-resampled.wav"
        run([tool("ffmpeg"), "-nostdin", "-v", "error", "-y", "-i", background,
             "-ar", str(info.samplerate), "-ac", str(info.channels), "-c:a", "pcm_f32le", converted])
        background = converted
    gaps = read_json(folder / "speech_regions.json")["gaps"]
    ranges = [(round(g["start"]*info.samplerate), round(g["end"]*info.samplerate)) for g in gaps]
    target = folder / "background-preserved.wav"
    partial = target.with_suffix(".partial.wav")
    with sf.SoundFile(str(source)) as original, sf.SoundFile(str(background)) as bg, sf.SoundFile(
            str(partial), "w", samplerate=info.samplerate, channels=info.channels, subtype="FLOAT") as output:
        offset = 0
        while offset < info.frames:
            count = min(info.samplerate*30, info.frames-offset)
            raw = original.read(count, dtype="float32", always_2d=True)
            stem = bg.read(count, dtype="float32", always_2d=True)
            mixed = np.zeros_like(raw)
            mixed[:len(stem)] = stem*.65
            for first, last in ranges:
                lo, hi = max(0, first-offset), min(count, last-offset)
                if hi > lo: mixed[lo:hi] = raw[lo:hi]
            output.write(mixed); offset += count
    partial.replace(target)
    return str(target)
