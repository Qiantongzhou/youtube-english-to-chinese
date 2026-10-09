"""Approximate spoken length; punctuation is excluded, English/numeric runs count once."""
import math
import re


def units(text):
    return len(re.findall(r"[\u3400-\u9fff]|[A-Za-z]+|\d+(?:\.\d+)?", str(text)))


def target_rate(settings):
    value = float(settings.get("chars_per_second", 4))
    if not math.isfinite(value) or not 2 <= value <= 8:
        raise ValueError("每秒字数应在 2 到 8 之间。")
    return value


def budget(segment, rate):
    duration = segment.get("speech_duration", segment["end"] - segment["start"])
    target = max(1, round(duration * rate))
    return {"target": target, "min": max(1, math.floor(target * .8)), "max": max(1, math.ceil(target * 1.2))}


def outside(segment, rate):
    bounds = budget(segment, rate)
    return not bounds["min"] <= units(segment.get("zh", "")) <= bounds["max"]
