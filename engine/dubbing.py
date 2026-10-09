"""Render complete voice clips and an editable placement manifest without truncation."""
import csv
import math
from pathlib import Path
from core import emit, read_json, run, tool, tempo_filter, write_json
from speech_rate import units


def clip_key(entry):
    """A position belongs to this generated recording, not merely its row number."""
    return (str(entry["id"]), str(entry["zh"]), Path(str(entry["file"])).name)


def resolve_clip(entry, folder):
    """Prefer the opened project's copy when an older project has been moved."""
    stored = Path(str(entry["file"]))
    candidates = [Path(folder) / "配音片段" / stored.name]
    if not stored.is_absolute(): candidates.append(Path(folder) / stored)
    candidates.append(stored)
    for candidate in candidates:
        if candidate.is_file(): return candidate.resolve()
    raise ValueError(f"找不到第 {entry.get('id', '?')} 段配音文件：{stored.name}。请恢复项目的配音片段文件夹。")


def refresh_placement_notes(timeline, allowed=4):
    """Recompute temporal facts after dragging, without reordering clip identities."""
    cursor = 0.0
    for entry in sorted(timeline, key=lambda row: (row["start"], row["end"])):
        notes = [note for note in entry.get("note", "").split("；")
                 if note and note not in {"已后移", "与前句重叠，待手动调整", "超出人声段，原速保留", "时间轴手动位置"}
                 and not note.startswith("边界超差")]
        if "source_start" in entry: entry["start_error"] = entry["start"] - entry["source_start"]
        if "source_end" in entry: entry["end_error"] = entry["end"] - entry["source_end"]
        if "start_error" in entry and "end_error" in entry:
            entry["within_tolerance"] = max(abs(entry["start_error"]), abs(entry["end_error"])) <= allowed + .01
            if not entry["within_tolerance"]:
                notes.append(f"边界超差（开始{entry['start_error']:+.2f}s / 结束{entry['end_error']:+.2f}s）")
        if "block_end" in entry:
            entry["gap_overflow"] = max(0, entry["end"] - entry["block_end"])
            if entry["gap_overflow"] >= .01:
                entry["within_tolerance"] = False
                notes.append("超出人声段，原速保留")
        if entry.get("position_override"): notes.append("时间轴手动位置")
        if entry["start"] < cursor - .001: notes.append("与前句重叠，待手动调整")
        cursor = max(cursor, entry["end"])
        entry["note"] = "；".join(dict.fromkeys(notes))


def apply_positions(timeline, folder, strict=False, allowed=4):
    path = Path(folder) / "voice_positions.json"
    if not path.is_file(): return 0
    data = read_json(path)
    if not isinstance(data, dict) or data.get("version") != 1 or not isinstance(data.get("clips"), list):
        raise ValueError("配音位置文件格式无效，请在时间轴中重新保存位置。")
    by_key = {clip_key(entry): entry for entry in timeline}
    if len(by_key) != len(timeline):
        raise ValueError("配音时间表存在重复片段，请重新生成配音。")
    requested, seen, stale = [], set(), 0
    for saved in data["clips"]:
        try:
            key, start = clip_key(saved), float(saved["start"])
        except (KeyError, TypeError, ValueError) as exc:
            raise ValueError("配音位置文件缺少有效的片段信息。") from exc
        if not math.isfinite(start) or start < 0:
            raise ValueError("配音开始时间必须是非负秒数。")
        if key in seen: raise ValueError("配音位置文件包含重复片段，请重新保存位置。")
        seen.add(key)
        entry = by_key.get(key)
        if entry is None:
            stale += 1
            continue
        requested.append((entry, round(start * 24000) / 24000))
    if stale and strict:
        raise ValueError("保存的位置对应的文案或音频已经改变，请重新打开时间轴并保存当前位置。")
    for entry, start in requested:
        length = entry["end"] - entry["start"]
        delta = start - entry["start"]
        entry.update(start=start, end=start + length, manual=True, position_override=True)
        for field in ("start_error", "end_error"):
            if field in entry: entry[field] += delta
    refresh_placement_notes(timeline, allowed)
    if stale: emit("log", message=f"{stale} 个旧配音位置与新文案或音频不匹配，已保留新片段的原始位置。")
    return len(requested)


def remix_existing(project, folder):
    """Reuse fitted clips verbatim; never load a TTS or translation model."""
    import soundfile as sf
    folder = Path(folder)
    if (folder / "voice_edit.json").is_file():
        from voice_edit import timeline as edited_timeline
        entries = edited_timeline(project, folder)
        duration = max(float(project["duration"]), max((e["end"] for e in entries), default=0))
        save_timeline(entries, folder, duration)
        return duration, entries, len(entries)
    path = folder / "dub_timing.json"
    if not path.is_file(): raise ValueError("此项目还没有生成配音，请先生成一次中文配音。")
    timeline = read_json(path)
    if not isinstance(timeline, list) or not timeline:
        raise ValueError("此项目没有可编辑的配音片段。")
    rows = read_json(folder / "segments.json")
    if project.get("active_sentence_alignment"):
        from sentence_alignment import sentences
        baseline, prepared = folder / "alignment-source.json", folder / "sentence_segments.json"
        # Older projects may contain approved sentence rewrites from alignment rounds.
        # Reuse those only while their original source text still matches the editor.
        source_text = lambda values: [(str(row["id"]), str(row.get("zh", ""))) for row in values]
        if baseline.is_file() and prepared.is_file() and source_text(rows) == source_text(read_json(baseline)):
            rows = read_json(prepared)
        else:
            rows = sentences(rows)
    def matches(source):
        return len(source) == len(timeline) and all(
            str(row["id"]) == str(entry["id"]) and str(row.get("zh", "")) == str(entry.get("zh", ""))
            and ("source_ids" not in entry or row.get("source_ids", [row["id"]]) == entry["source_ids"])
            for row, entry in zip(source, timeline))
    if not matches(rows) and "active_sentence_alignment" not in project:
        # Some early exports omitted the flag; infer grouping only on an exact
        # text/identity match, never merely from the presence of an old cache.
        from sentence_alignment import sentences
        grouped = sentences(rows)
        if matches(grouped): rows = grouped
    if not matches(rows):
        raise ValueError("中文字幕已更改，与现有配音不一致。请先重新生成配音，再导出拖动后的位置。")
    for row, entry in zip(rows, timeline):
        clip = resolve_clip(entry, folder)
        info = sf.info(str(clip))
        if info.samplerate != 24000 or info.channels != 1 or info.frames <= 0:
            raise ValueError(f"第 {entry['id']} 段音频不是有效的 24 kHz 单声道配音，请重新生成配音。")
        start = float(entry["start"])
        if not math.isfinite(start) or start < 0:
            raise ValueError(f"第 {entry['id']} 段配音开始时间无效。")
        entry.update(file=str(clip), start=start, end=start + info.frames / 24000)
        entry.setdefault("source_start", float(row["start"]))
        entry.setdefault("source_end", float(row["end"]))
    from sentence_alignment import tolerance
    allowed = tolerance(project.get("settings", {}))
    count = apply_positions(timeline, folder, strict=True, allowed=allowed)
    refresh_placement_notes(timeline, allowed)
    duration = max(float(project["duration"]), max(entry["end"] for entry in timeline))
    save_timeline(timeline, folder, duration)
    return duration, timeline, count


def placement(segment, actual, cursor, rate=None, playback_speed=1.0):
    if not math.isfinite(playback_speed) or playback_speed <= 0:
        raise ValueError("统一语速必须是正数。")
    speed = playback_speed
    requested = segment.get("audio_start")
    if requested is not None and (not math.isfinite(float(requested)) or float(requested) < 0):
        raise ValueError("配音开始时间必须是非负秒数。")
    start = float(requested) if requested is not None else float(segment["start"])
    return start, speed


def render_dub(segments, raw_paths, folder, video_duration, rate=None, alignment_tolerance=None, playback_speed=1.0,
               generated_model="", generated_voice=""):
    import numpy as np
    import soundfile as sf
    folder = Path(folder)
    clips = folder / "配音片段"
    clips.mkdir(exist_ok=True)
    timeline, cursor = [], 0.0
    for index, (segment, raw) in enumerate(zip(segments, raw_paths, strict=True)):
        info = sf.info(str(raw))
        actual = info.frames / info.samplerate
        start, speed = placement(segment, actual, cursor, rate, playback_speed)
        # Distinct fitted paths keep an earlier, better alignment round reusable.
        clip = clips / f"{index + 1:04d}_{Path(raw).stem}_{speed:.6f}.wav"
        partial = clip.with_suffix(".partial.wav")
        run([tool("ffmpeg"), "-nostdin", "-v", "error", "-y", "-i", raw,
             "-af", tempo_filter(speed), "-ar", "24000", "-ac", "1", partial])
        partial.replace(clip)
        fitted = sf.info(str(clip))
        start = round(start * 24000) / 24000
        end = start + fitted.frames / 24000
        notes = []
        if end - start > segment["end"] - segment["start"] + .05: notes.append("超长配音已完整保留")
        measured_rate = units(segment.get("zh", "")) / max(.001, end - start)
        if rate and abs(measured_rate / rate - 1) > .2: notes.append("字速偏离目标，建议调整文案")
        if start > segment["start"] + .001: notes.append("已后移")
        if start < cursor - .001: notes.append("与前句重叠，待手动调整")
        timeline.append({"id": segment["id"], "start": start, "end": end, "speed": speed,
                         "generated_model": generated_model, "generated_voice": generated_voice,
                         "source_start": float(segment["start"]), "source_end": float(segment["end"]),
                         "raw_duration": actual, "chars_per_second": measured_rate,
                         "file": str(clip.resolve()), "source_ids": segment.get("source_ids", [segment["id"]]), "zh": segment.get("zh", ""), "note": "；".join(notes)})
        cursor = max(cursor, end)
    if alignment_tolerance is not None or all(s.get("speech_block") is not None for s in segments):
        from sentence_alignment import schedule
        placements = schedule(segments, [entry["end"] - entry["start"] for entry in timeline], alignment_tolerance or 4)
        for entry, point in zip(timeline, placements):
            entry.update(point)
            entry["note"] = "；".join(note for note in entry["note"].split("；") if note not in {"已后移", "与前句重叠，待手动调整"})
            if not point["within_tolerance"]:
                entry["note"] += f"；边界超差（开始{point['start_error']:+.2f}s / 结束{point['end_error']:+.2f}s）"
            if point["manual"]: entry["note"] += "；手动位置"
        cursor = 0.0
        for entry in timeline:
            if entry["start"] < cursor - .001:
                entry["note"] += "；与前句重叠，待手动调整"
            if entry.get("gap_overflow", 0) > .01:
                entry["note"] += "；超出人声段，原速保留"
            cursor = max(cursor, entry["end"])
    apply_positions(timeline, folder, allowed=alignment_tolerance or 4)
    duration = max(video_duration, max((entry["end"] for entry in timeline), default=0))
    save_timeline(timeline, folder, duration)
    return duration, timeline


def save_timeline(timeline, folder, duration):
    import numpy as np
    import soundfile as sf
    folder = Path(folder)
    partial = folder / "dub.partial.wav"
    with sf.SoundFile(str(partial), "w+", samplerate=24000, channels=1, subtype="FLOAT") as out:
        remaining = math.ceil(duration * 24000)
        while remaining:
            count = min(remaining, 24000 * 30)
            out.write(np.zeros(count, dtype=np.float32)); remaining -= count
        for entry in timeline:
            audio, _ = sf.read(entry["file"], dtype="float32")
            target = round(entry["start"] * 24000)
            out.seek(target); existing = out.read(len(audio), dtype="float32")
            out.seek(target); out.write(existing + audio)
    partial.replace(folder / "dub.wav")
    write_json(folder / "dub_timing.json", timeline)
    with (folder / "配音时间表.csv").open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.writer(handle)
        writer.writerow(["句子", "中文配音文案", "实际开始秒", "实际结束秒", "倍速", "原始配音秒", "实际字每秒", "音频文件", "开始误差秒", "结束误差秒", "待调整"])
        for entry in timeline:
            writer.writerow([entry.get(k, "") for k in ["id", "zh", "start", "end", "speed", "raw_duration", "chars_per_second", "file", "start_error", "end_error", "note"]])



def protect_speech_gaps(segments, timeline, folder, video_duration, allowed=4):
    """Legacy entry point: preserve natural speed and allow overflow/overlap."""
    duration = max(video_duration, max((entry["end"] for entry in timeline), default=0))
    save_timeline(timeline, folder, duration)
    return 0
