"""Non-destructive source-coordinate video edits and shared subtitle retiming."""
import math
from pathlib import Path
from core import read_json


def kept_ranges(folder, source_duration, total_duration):
    path = Path(folder) / "video_edit.json"
    if not path.is_file(): return None
    data = read_json(path)
    stored_duration = float(data["source_duration"])
    if data.get("version") != 1 or not math.isfinite(stored_duration) or abs(stored_duration - source_duration) > .05:
        raise ValueError("视频剪切记录与原视频时长不匹配。")
    end, ids, kept, deleted = 0.0, set(), [], False
    for row in data["segments"]:
        start, stop, ident = float(row["start"]), float(row["end"]), int(row["id"])
        if (not math.isfinite(start) or not math.isfinite(stop) or ident <= 0 or ident in ids
                or abs(start - end) > .00001 or stop <= start or stop > source_duration + .00001
                or not isinstance(row["deleted"], bool)):
            raise ValueError("视频剪切区间无效，请重新打开项目检查。")
        ids.add(ident); end = stop
        if row["deleted"]:
            deleted = True
        else:
            # Preserve the existing final-frame extension only when the final source section survives.
            stop = max(stop, total_duration) if abs(stop - source_duration) < .00001 else stop
            if kept and abs(kept[-1][1] - start) < .00001: kept[-1] = (kept[-1][0], stop)
            else: kept.append((start, stop))
    if abs(end - source_duration) > .00001 or not kept:
        raise ValueError("视频剪切记录必须覆盖原视频，并保留至少一段视频。")
    return kept if deleted else None


def retime(rows, kept):
    if kept is None: return rows
    result, offset = [], 0.0
    for start, end in kept:
        for row in rows:
            a, b = max(start, float(row["start"])), min(end, float(row["end"]))
            if b > a:
                result.append({**row, "start": offset + a - start, "end": offset + b - start})
        offset += end - start
    return sorted(result, key=lambda r: (r["start"], r["end"]))


def export_graph(audio_args, video_filters, kept, total_duration, has_source_audio=True):
    """Video selection streams frames once; sample-accurate audio trims are concatenated."""
    inputs, graph, audio_filter, maps = [], [], None, []
    index = 0
    while index < len(audio_args):
        flag = audio_args[index]
        if flag in {"-map", "-af", "-filter_complex"}:
            value = audio_args[index + 1]
            if flag == "-map": maps.append(value)
            elif flag == "-af": audio_filter = value
            else: graph.append(value)
            index += 2
        else:
            inputs.append(flag); index += 1
    audio = maps[-1]
    if audio == "0:a:0" and not has_source_audio:
        graph.append("anullsrc=r=48000:cl=stereo[silent_source]"); audio = "[silent_source]"
    label = audio if audio.startswith("[") else f"[{audio}]"
    preparation = ([audio_filter] if audio_filter else []) + ["asetpts=PTS-STARTPTS", f"apad=whole_dur={total_duration:.9f}", f"atrim=duration={total_duration:.9f}"]
    count = len(kept)
    graph.append(label + ",".join(preparation) + f",asplit={count}" + "".join(f"[cut_in{i}]" for i in range(count)))
    for i, (start, end) in enumerate(kept):
        graph.append(f"[cut_in{i}]atrim=start={start:.9f}:end={end:.9f},asetpts=PTS-STARTPTS[cut_part{i}]")
    graph.append("".join(f"[cut_part{i}]" for i in range(count)) + f"concat=n={count}:v=0:a=1[cut_audio]")
    select = "+".join(f"gte(t,{start:.9f})*lt(t,{end:.9f})" for start, end in kept)
    elapsed = "+".join(f"min(max(T-{start:.9f},0),{end-start:.9f})" for start, end in kept)
    # The final ASS file is already retimed; burn it after cutting and resetting timestamps.
    captions = [f for f in video_filters if f.startswith("ass=")]
    base = [f for f in video_filters if not f.startswith("ass=")]
    filters = base + ["setpts=PTS-STARTPTS", f"select='{select}'", f"setpts='({elapsed})/TB'", *captions]
    graph.append("[0:v:0]" + ",".join(filters) + "[cut_video]")
    return inputs, ";".join(graph)
