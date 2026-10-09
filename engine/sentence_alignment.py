"""Chinese sentence grouping and bounded, non-overlapping placement against source anchors."""
import math
import re
from speech_rate import units


def tolerance(settings):
    value = float(settings.get("alignment_tolerance", 4))
    if not math.isfinite(value) or not 1 <= value <= 4:
        raise ValueError("配音对齐容差应在 1 到 4 秒之间。")
    return value


def sentences(rows):
    result, pending = [], []
    def flush():
        if not pending: return
        source_ids = list(dict.fromkeys(part["source_id"] for part in pending))
        entry = {"id": len(result) + 1, "start": pending[0]["start"], "end": pending[-1]["end"],
                 "zh": "".join(part["zh"] for part in pending),
                 "en": " ".join(dict.fromkeys(part["en"] for part in pending)),
                 "source_ids": source_ids, "source_parts": list(pending)}
        if pending[0].get("speech_block") is not None:
            entry.update(speech_block=pending[0]["speech_block"], block_start=pending[0]["block_start"], block_end=pending[0]["block_end"],
                         speech_duration=sum(part.get("speech_duration", part["end"]-part["start"]) for part in pending))
        if pending[0].get("audio_start") is not None: entry["audio_start"] = pending[0]["audio_start"]
        result.append(entry); pending.clear()
    for row in rows:
        # A manual position is an explicit boundary, even if punctuation is missing.
        if row.get("audio_start") is not None or (pending and row.get("speech_block") != pending[-1].get("speech_block")):
            flush()
        text = str(row["zh"]).strip()
        parts = re.findall(r'[^。！？]+[。！？]+[”’」』"]*|[^。！？]+$|[。！？]+[”’」』"]*', text)
        weights = [max(1, units(part)) for part in parts]
        total, elapsed = sum(weights), 0
        for index, (part, weight) in enumerate(zip(parts, weights)):
            start = row["start"] + (row["end"] - row["start"]) * elapsed / total
            elapsed += weight
            end = row["start"] + (row["end"] - row["start"]) * elapsed / total
            entry = {"source_id": row["id"], "start": start, "end": end, "zh": part, "en": row.get("en", "")}
            if row.get("speech_block") is not None:
                entry.update(speech_block=row["speech_block"], block_start=row["block_start"], block_end=row["block_end"],
                             speech_duration=row.get("speech_duration", row["end"]-row["start"])*weight/total)
            if index == 0 and row.get("audio_start") is not None: entry["audio_start"] = row["audio_start"]
            pending.append(entry)
            if re.search(r'[。！？][”’」』"]*$', part): flush()
    flush()
    return result


def schedule(rows, durations, allowed=2):
    if len(rows) != len(durations): raise ValueError("配音句子和音频数量不一致。")
    if any(not math.isfinite(d) or d <= 0 for d in durations): raise ValueError("配音时长无效。")
    result, seen_blocks = [], set()
    for row, duration in zip(rows, durations):
        block = row.get("speech_block")
        first = block is not None and block not in seen_blocks
        requested = row.get("audio_start")
        start = float(requested) if requested is not None else float(row["block_start"] if first else row["start"])
        if not math.isfinite(start) or start < 0: raise ValueError("配音开始时间必须是非负秒数。")
        if block is not None: seen_blocks.add(block)
        end = start + duration
        point = {"start": start, "end": end, "start_error": start-row["start"], "end_error": end-row["end"],
                 "manual": requested is not None,
                 "within_tolerance": max(abs(start-row["start"]), abs(end-row["end"])) <= allowed+.01}
        if block is not None:
            overflow = max(0, end-row["block_end"])
            point.update(speech_block=block, block_start=row["block_start"], block_end=row["block_end"], gap_overflow=overflow)
            point["within_tolerance"] = point["within_tolerance"] and overflow < .01
        result.append(point)
    return result


def schedule_blocks(rows, durations, allowed):
    return schedule(rows, durations, allowed)
