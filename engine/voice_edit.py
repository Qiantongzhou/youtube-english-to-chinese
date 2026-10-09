"""Editable voice clips are independent of the original many-to-many subtitle rows."""
import math
import uuid
from pathlib import Path
from core import read_json, write_json, run, tool, tempo_filter
from voice_models import model_identity


def load(folder):
    document = read_json(Path(folder) / "voice_edit.json")
    if document.get("version") != 1 or not isinstance(document.get("clips"), list):
        raise ValueError("时间轴配音稿格式无效。")
    source = read_json(Path(folder) / "segments.json")
    signature = lambda rows: [(str(row["id"]), str(row.get("zh", ""))) for row in rows]
    if signature(source) != signature(document.get("source_text", [])):
        raise ValueError("原字幕表格的中文已改变。时间轴配音稿独立保存；请还原原字幕表格的修改，并在时间轴右键编辑中文，避免覆盖片段。")
    ids = set()
    for clip in document["clips"]:
        ident = int(clip["id"])
        if ident <= 0 or ident in ids: raise ValueError("时间轴片段编号重复或无效。")
        ids.add(ident)
        if not str(clip.get("zh", "")).strip(): raise ValueError(f"配音 {ident} 的中文不能为空。")
        for key in ("voice", "generated_voice", "generated_model", "speaker"):
            if not isinstance(clip.get(key, ""), str):
                raise ValueError(f"配音 {ident} 的人物或音色设置无效，请在时间轴重新选择。")
        for key in ("start", "duration"):
            value = float(clip[key])
            if not math.isfinite(value) or value < 0 or (key == "duration" and value == 0):
                raise ValueError(f"配音 {ident} 的时间无效。")
    return document


def generate(project, folder, synthesize, progress):
    import soundfile as sf
    folder = Path(folder)
    document = load(folder)
    requested = project.get("voice_edit_ids")
    clips = [c for c in document["clips"] if requested is None or c["id"] in requested]
    if requested and len(clips) != len(set(requested)):
        raise ValueError("选中的配音片段已不存在，请重新打开项目。")
    if not clips: return
    # Explicit regeneration creates new recordings without destroying the prior cache.
    cache = folder / "voice_cache" / ("edit-" + uuid.uuid4().hex) if project.get("voice_edit_force") else None
    try: speed = min(1.5, max(.75, float(project["settings"].get("voice_speed", 1))))
    except (TypeError, ValueError): speed = 1
    directory = folder / "配音片段"
    directory.mkdir(exist_ok=True)
    # Group by effective voice so local dual-GPU batches still share a speaker.
    # Override only this group's settings; never change the project's default voice.
    groups = {}
    for clip in clips:
        voice = clip.get("voice") or project["settings"].get("voice", "Serena")
        groups.setdefault(voice, []).append(clip)
    completed = 0
    for voice, group in groups.items():
        group_project = {**project, "settings": {**project["settings"], "voice": voice}}
        generated_model = model_identity(group_project["settings"])
        progress("tts", f"正在生成音色 {voice} 的 {len(group)} 段配音…", completed / len(clips))
        raws = synthesize(group_project, folder, group, cache)
        for clip, raw in zip(group, raws, strict=True):
            target = directory / f"edit_{clip['id']}_{uuid.uuid4().hex}.wav"
            partial = target.with_suffix(".partial.wav")
            run([tool("ffmpeg"), "-nostdin", "-v", "error", "-y", "-i", raw,
                 "-af", tempo_filter(speed), "-ar", "24000", "-ac", "1", partial])
            info = sf.info(str(partial))
            if info.frames <= 0: raise ValueError("生成了空音频，请重新生成本段。")
            partial.replace(target)
            clip.update(file=str(target.resolve()), generated_zh=clip["zh"], generated_voice=voice,
                        generated_model=generated_model,
                        duration=info.frames / 24000, speed=speed, raw_duration=sf.info(str(raw)).duration)
            # Persist each finished clip; cancellation leaves other recordings intact.
            write_json(folder / "voice_edit.json", document)
            completed += 1
            progress("tts", f"已更新配音 {clip['id']}（{voice}），开始位置保持 {clip['start']:.3f} 秒", completed / len(clips))
    project["timeline_edited"] = True
    project.pop("voice_edit_ids", None)
    project.pop("voice_edit_force", None)


def timeline(project, folder):
    import soundfile as sf
    from dubbing import resolve_clip, refresh_placement_notes
    from speech_rate import units
    document = load(folder)
    entries = []
    from video_edit import kept_ranges
    total = max(project["duration"], max((float(c["start"]) + float(c["duration"]) for c in document["clips"]), default=0))
    kept = kept_ranges(folder, project["duration"], total)
    for clip in document["clips"]:
        if kept is not None and not any(float(clip["start"]) < end and float(clip["start"]) + float(clip["duration"]) > start for start, end in kept):
            # Keep the editable source clip in voice_edit.json, including pending recordings.
            # Restoring the deleted video range restores its generation requirement too.
            continue
        if (clip.get("generated_zh") != clip["zh"] or not clip.get("file")
                or (clip.get("voice") and clip.get("generated_voice") != clip["voice"])):
            raise ValueError(f"配音 {clip['id']} 仍待生成，请右键重新生成语音后再导出。")
        file = resolve_clip(clip, folder)
        info = sf.info(str(file))
        if info.samplerate != 24000 or info.channels != 1 or info.frames <= 0:
            raise ValueError(f"配音 {clip['id']} 音频无效，请重新生成本段。")
        start = round(float(clip["start"]) * 24000) / 24000
        duration = info.frames / 24000
        entries.append(dict(id=clip["id"], zh=clip["zh"], start=start, end=start + duration,
            file=str(file), source_ids=clip.get("source_ids", []), speed=clip.get("speed", 1),
            voice=clip.get("voice", ""), generated_voice=clip.get("generated_voice", ""), speaker=clip.get("speaker", ""),
            generated_model=clip.get("generated_model", ""),
            raw_duration=clip.get("raw_duration", duration), chars_per_second=units(clip["zh"]) / duration,
            manual=True, position_override=True, note="时间轴编辑"))
    refresh_placement_notes(entries)
    return entries
