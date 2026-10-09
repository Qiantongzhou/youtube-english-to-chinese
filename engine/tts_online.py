"""Edge online voices, converted to the same cached WAVs as local synthesis."""
import asyncio
import hashlib
import json
from pathlib import Path
from tempfile import TemporaryDirectory

from core import emit, run, tool

VOICES = {
    "zh-CN-XiaoxiaoNeural", "zh-CN-XiaoyiNeural", "zh-CN-YunxiNeural",
    "zh-CN-YunyangNeural", "zh-CN-YunjianNeural", "zh-CN-YunxiaNeural",
}


def generate(segments, cache, voice, progress):
    if voice not in VOICES:
        raise ValueError("未知在线音色，请重新选择制作偏好中的在线中文音色。")
    import soundfile as sf
    cache = Path(cache)
    cache.mkdir(parents=True, exist_ok=True)
    paths, pending, counts = [], {}, {}
    for segment in segments:
        text = segment["zh"]
        signature = hashlib.sha256(json.dumps(
            ["edge-online-v1", voice, text, "+0%", "+0Hz"],
            ensure_ascii=False).encode("utf-8")).hexdigest()[:24]
        raw = (cache / (signature + ".wav")).resolve()
        paths.append(raw)
        try:
            if raw.is_file() and sf.info(str(raw)).frames > 0:
                continue
        except (RuntimeError, ValueError):
            pass
        pending[signature] = (text, raw)
        counts[signature] = counts.get(signature, 0) + 1
    completed = len(segments) - sum(counts.values())
    total = max(1, len(segments))
    progress("tts", f"在线配音：已复用 {completed}/{len(segments)} 句缓存", completed / total * .9)
    if not pending:
        return paths
    try:
        import edge_tts
    except ImportError as exc:
        raise RuntimeError("在线配音依赖未安装，请在环境页运行「安装 / 修复完整环境」。") from exc
    emit("log", message=f"使用微软 Edge 在线音色 {voice}；待配音文本发送到微软服务，不使用本机 GPU。")

    async def synthesize(text, destination):
        for attempt in range(3):
            try:
                communicate = edge_tts.Communicate(
                    text, voice, rate="+0%", pitch="+0Hz",
                    connect_timeout=15, receive_timeout=45)
                await asyncio.wait_for(communicate.save(str(destination)), timeout=180)
                if not destination.is_file() or destination.stat().st_size == 0:
                    raise RuntimeError("返回的音频为空")
                return
            except Exception as exc:
                if attempt == 2:
                    raise RuntimeError(
                        f"在线配音请求失败（{type(exc).__name__}），请检查网络或稍后继续；"
                        "已完成缓存保留，也可改选本地音色。") from None
                emit("log", message=f"在线配音请求未完成，准备第 {attempt + 2}/3 次尝试。")
                await asyncio.sleep(2 * (attempt + 1))

    # Sequential requests avoid service throttling; duplicate text shares a job.
    with TemporaryDirectory(prefix="edge-", dir=cache) as temp:
        mp3, wav = Path(temp) / "speech.mp3", Path(temp) / "speech.wav"
        for signature, (text, raw) in pending.items():
            progress("tts", f"正在联网生成配音 · 已完成 {completed}/{len(segments)} 句",
                     completed / total * .9)
            asyncio.run(synthesize(text, mp3))
            run([tool("ffmpeg"), "-nostdin", "-v", "error", "-y", "-i", mp3,
                 "-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le", wav])
            if sf.info(str(wav)).frames == 0:
                raise RuntimeError("在线配音转换后为空，请重新生成本句。")
            wav.replace(raw)
            completed += counts[signature]
            progress("tts", f"在线配音完成 {completed}/{len(segments)} 句", completed / total * .9)
    return paths
