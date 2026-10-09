"""Project format, subtitle formatting and process boundary shared by the workers."""
from __future__ import annotations

import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
from urllib.parse import urlparse, parse_qs

ROOT = Path(os.environ.get("VIDEOCN_ROOT", Path(__file__).resolve().parents[1]))


def emit(kind="progress", **data):
    print(json.dumps({"type": kind, **data}, ensure_ascii=False), flush=True)


def write_json(path, data):
    path = Path(path)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(path)


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def youtube_url(value):
    parsed = urlparse(value.strip())
    host = (parsed.hostname or "").lower()
    if parsed.scheme not in {"http", "https"} or parsed.username or parsed.password:
        raise ValueError("请输入完整的 YouTube 视频网址。")
    if host == "youtu.be":
        ident = parsed.path.strip("/")
    elif host in {"youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com"}:
        if parsed.path == "/watch":
            ident = parse_qs(parsed.query).get("v", [""])[0]
        elif parsed.path.startswith(("/shorts/", "/live/", "/embed/")):
            ident = parsed.path.split("/")[2]
        else:
            ident = ""
    else:
        ident = ""
    if not re.fullmatch(r"[A-Za-z0-9_-]{11}", ident):
        raise ValueError("仅支持单个 YouTube 视频链接，不支持频道或播放列表。")
    return "https://www.youtube.com/watch?v=" + ident


def tool(name):
    candidates = [ROOT / "tools" / (name + ".exe"), ROOT / "tools" / name / (name + ".exe")]
    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)
    found = shutil.which(name)
    if found:
        return found
    raise RuntimeError(f"缺少 {name}，请点击「安装 / 修复环境」。")


def process_env():
    env = os.environ.copy()
    env.update(PYTHONUTF8="1", PYTHONUNBUFFERED="1", VIDEOCN_ROOT=str(ROOT),
               HF_HOME=str(ROOT / "models" / "huggingface"),
               TORCH_HOME=str(ROOT / "models" / "torch"))
    env["PATH"] = str(ROOT / "tools") + os.pathsep + env.get("PATH", "")
    return env


def run(args, cwd=None, on_line=None):
    tail = []
    with subprocess.Popen([str(a) for a in args], cwd=cwd, env=process_env(),
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          text=True, encoding="utf-8", errors="replace",
                          creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)) as proc:
        for line in proc.stdout:
            line = line.rstrip()
            tail.append(line)
            tail = tail[-35:]
            if on_line:
                on_line(line)
        code = proc.wait()
    if code:
        raise RuntimeError(f"{Path(str(args[0])).name} 退出代码 {code}\n" + "\n".join(tail))
    return "\n".join(tail)


def probe(path):
    result = subprocess.run([tool("ffprobe"), "-v", "error", "-show_format", "-show_streams",
                             "-of", "json", str(path)], capture_output=True, text=True,
                            encoding="utf-8", errors="replace", env=process_env(),
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if result.returncode:
        raise RuntimeError("无法读取媒体：" + result.stderr[-2000:])
    return json.loads(result.stdout)


def validate_segments(segments, duration=None, require_zh=True):
    if not segments:
        raise ValueError("没有可用的语音片段。请确认视频含有清晰的英文讲话。")
    previous_end = 0.0
    for i, s in enumerate(segments):
        start, end = float(s["start"]), float(s["end"])
        if not math.isfinite(start) or not math.isfinite(end) or start < 0 or end <= start:
            raise ValueError(f"第 {i+1} 行起止时间无效。")
        if start < previous_end - 0.001:
            raise ValueError(f"第 {i+1} 行与前一句重叠，请修正时间。")
        if duration is not None and end > duration + 0.05:
            raise ValueError(f"第 {i+1} 行超出视频时长。")
        if require_zh and not str(s.get("zh", "")).strip():
            raise ValueError(f"第 {i+1} 行缺少中文翻译。")
        audio_start = s.get("audio_start")
        if audio_start is not None and (not math.isfinite(float(audio_start)) or float(audio_start) < 0):
            raise ValueError(f"第 {i+1} 行配音开始时间无效。")
        previous_end = end


def timestamp(seconds, ass=False):
    units = 100 if ass else 1000
    ticks = max(0, round(seconds * units))
    whole, fraction = divmod(ticks, units)
    minutes, sec = divmod(whole, 60)
    hour, minute = divmod(minutes, 60)
    return (f"{hour}:{minute:02}:{sec:02}.{fraction:02}" if ass else
            f"{hour:02}:{minute:02}:{sec:02},{fraction:03}")


def _subtitle_width(value):
    # Latin characters are roughly half the width of Chinese at this font.
    return sum(1 if "\u2e80" <= char <= "\uffff" else .55 for char in value)


def split_subtitle_text(text, max_width=20):
    text = re.sub(r"\s+", " ", str(text)).strip()
    tokens = re.findall(r"[A-Za-z0-9]+(?:['’.-][A-Za-z0-9]+)*|.", text)
    closing = "，。！？；：、,.!?;:)]}）】》”’"
    chunks, current = [], ""
    for token in tokens:
        if token.isspace():
            if current and not current.endswith(" "):
                current += " "
            continue
        candidate = current + token
        if current.strip() and _subtitle_width(candidate) > max_width and token not in closing:
            chunks.append(current.rstrip())
            current = token
        else:
            current = candidate
    if current.strip():
        chunks.append(current.rstrip())
    return chunks


def subtitle_lines(text, line_width=20):
    text = str(text).replace("\r", " ").replace("\n", " ").strip()
    # Keep English words, contractions and names such as GPT-6 in one piece.
    tokens = re.findall(r"[A-Za-z0-9]+(?:['’.-][A-Za-z0-9]+)*|.", text)

    if not tokens or _subtitle_width(text) <= line_width:
        return [text] if text else []

    # Select one token boundary closest to the visual midpoint. This guarantees
    # at most two lines and never cuts an English word.
    best = None
    closing = "，。！？；：、,.!?;:)]}）】》”’"
    opening = "([{（【《“‘"
    for index in range(1, len(tokens)):
        left = "".join(tokens[:index]).strip()
        right = "".join(tokens[index:]).strip()
        if not left or not right:
            continue
        penalty = abs(_subtitle_width(left) - _subtitle_width(right))
        if right[0] in closing:
            penalty += line_width
        if left[-1] in opening:
            penalty += line_width
        candidate = (penalty, max(_subtitle_width(left), _subtitle_width(right)), left, right)
        if best is None or candidate < best:
            best = candidate
    return [best[2], best[3]] if best else [text]


def ass_text(text):
    # Do not interpret user subtitles as ASS overrides or drawing commands.
    def escape(line):
        return line.replace("\\", "＼").replace("{", "｛").replace("}", "｝")
    return "\\N".join(escape(line) for line in subtitle_lines(text))


def subtitle_timeline(segments, max_width=20):
    cues = []
    for segment in segments:
        chunks = split_subtitle_text(segment["zh"], max_width)
        if not chunks:
            continue
        start, end = float(segment["start"]), float(segment["end"])
        weights = [max(.01, _subtitle_width(chunk)) for chunk in chunks]
        total, elapsed = sum(weights), 0.0
        for index, (chunk, weight) in enumerate(zip(chunks, weights)):
            cue_start = start + (end - start) * elapsed / total
            elapsed += weight
            cue_end = end if index == len(chunks) - 1 else start + (end - start) * elapsed / total
            cues.append({"start": cue_start, "end": cue_end, "zh": chunk})
    return cues


def subtitles(segments, folder, stem="中文配音", max_width=20):
    folder = Path(folder)
    try:
        max_width = min(40, max(10, int(round(float(max_width)))))
    except (TypeError, ValueError):
        max_width = 20
    segments = subtitle_timeline(segments, max_width)
    srt = "\n\n".join(f'{i+1}\n{timestamp(s["start"])} --> {timestamp(s["end"])}\n'
                      + "\n".join(subtitle_lines(s["zh"]))
                      for i, s in enumerate(segments)) + "\n"
    (folder / (stem + ".srt")).write_text(srt, encoding="utf-8-sig")
    header = """[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Microsoft YaHei,52,&H00FFFFFF,&H000000FF,&H00181714,&H80000000,-1,0,0,0,100,100,0,0,1,3,1,2,90,90,65,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""
    lines = [f'Dialogue: 0,{timestamp(s["start"], True)},{timestamp(s["end"], True)},Default,,0,0,0,,{ass_text(s["zh"])}' for s in segments]
    (folder / (stem + ".ass")).write_text(header + "\n".join(lines) + "\n", encoding="utf-8-sig")


def tempo_filter(ratio):
    if not math.isfinite(ratio) or ratio <= 0:
        raise ValueError("无效的配音速度。")
    factors = []
    while ratio > 2:
        factors.append(2.0)
        ratio /= 2
    while ratio < 0.5:
        factors.append(0.5)
        ratio /= 0.5
    factors.append(ratio)
    return ",".join(f"atempo={f:.8f}" for f in factors)
