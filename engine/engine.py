"""CLI for the desktop app. stdout is JSONL; worker libraries cannot corrupt it."""
from __future__ import annotations
import argparse
from datetime import datetime
import importlib.util
import json
from pathlib import Path
import sys
import traceback
from core import ROOT, emit, read_json, write_json, run, tool, youtube_url
from browser_auth import cookie_arguments, download_error, auth_description


class WorkerFailure(RuntimeError):
    """A worker's concise error; its full traceback has already been logged."""


def check_login(path):
    request = read_json(path)
    url = youtube_url(request["source"])
    if request.get("cookie_source") not in {"edge", "file"}:
        raise ValueError("请先选择 Edge 登录状态或 Cookie 文件。")
    emit(stage="download", message="正在使用所选登录方式测试视频访问…", percent=0)
    emit("auth", message=auth_description(request))
    try:
        # Print only a title, never raw cookies, signed video URLs or response headers.
        run([tool("yt-dlp"), "--ignore-config", "--no-plugin-dirs", "--no-playlist",
             "--no-color", "--encoding", "utf-8", "--socket-timeout", "20", "--retries", "0",
             "--js-runtimes", "deno:" + tool("deno"), *cookie_arguments(request),
             "--skip-download", "--print", "title", "--", url])
    except RuntimeError as exc:
        raise RuntimeError(download_error(str(exc), request)) from exc
    emit("login_checked", message="已通过所选登录方式访问此视频，可以尝试下载。")


def worker(stage, project):
    worker_error = None
    def receive(line):
        nonlocal worker_error
        try:
            event = json.loads(line)
            if isinstance(event, dict) and "type" in event:
                if event["type"] == "error":
                    worker_error = str(event.get("message", "处理失败，请查看详细日志。"))
                    return
                emit(**{"kind": event.pop("type"), **event})
                return
        except (ValueError, TypeError):
            pass
        if line:
            emit("log", message=line)
    try:
        run([sys.executable, Path(__file__).with_name("worker.py"), stage, str(project)], on_line=receive)
    except RuntimeError:
        if worker_error:
            raise WorkerFailure(worker_error) from None
        raise
    if worker_error:
        raise WorkerFailure(worker_error)


def check():
    checks = []
    for name in ("ffmpeg", "ffprobe", "yt-dlp", "deno", "ollama"):
        try:
            checks.append({"name": name, "ok": True, "detail": tool(name)})
        except RuntimeError as exc:
            checks.append({"name": name, "ok": False, "detail": str(exc)})
    for name in ("torch", "faster_whisper", "qwen_tts", "demucs", "soundfile", "requests", "edge_tts"):
        checks.append({"name": name, "ok": importlib.util.find_spec(name) is not None})
    try:
        import torch
        checks.append({"name": "CUDA", "ok": torch.cuda.is_available(),
                       "detail": torch.cuda.get_device_name(0) if torch.cuda.is_available() else "未发现可用 CUDA；请安装 GPU 环境或选择 CPU。"})
    except ImportError:
        checks.append({"name": "CUDA", "ok": False, "detail": "PyTorch 尚未安装"})
    for label, glob in [
        ("高质量 / Whisper", "whisper/models--Systran--faster-whisper-large-v3/snapshots/*/model.bin"),
        ("高质量 / 中文配音", "huggingface/hub/models--Qwen--Qwen3-TTS-12Hz-1.7B-CustomVoice/snapshots/*/model.safetensors"),
        ("高质量 / 翻译", "ollama/manifests/registry.ollama.ai/library/qwen3/14b"),
        ("快速 / Whisper", "whisper/models--Systran--faster-whisper-medium/snapshots/*/model.bin"),
        ("快速 / 中文配音", "huggingface/hub/models--Qwen--Qwen3-TTS-12Hz-0.6B-CustomVoice/snapshots/*/model.safetensors"),
        ("快速 / 翻译", "ollama/manifests/registry.ollama.ai/library/qwen3/8b")]:
        available = any(p.is_file() and p.stat().st_size > 0 for p in (ROOT / "models").glob(glob))
        checks.append({"name": label, "ok": available, "detail": "已缓存" if available else "尚未下载此模型"})
    emit("check", checks=checks)


def execute(command, path, clip_id=None):
    path = Path(path).resolve()
    if command == "prepare":
        request = read_json(path)
        source = request["source"].strip()
        if not Path(source).is_file():
            request["source"] = youtube_url(source)
        folder = Path(request["output_dir"]) / (datetime.now().strftime("%Y%m%d_%H%M%S_") + __import__("uuid").uuid4().hex[:6])
        folder.mkdir(parents=True, exist_ok=False)
        path = folder / "project.json"
        write_json(path, {"version": 1, "settings": request, "status": "created", "completed": []})
        emit("project", path=str(path))
    project = read_json(path)
    project.setdefault("completed", [])
    if command == "render" and (path.parent / "voice_edit.json").is_file(): command = "voice-render"
    if command in {"voice-generate", "voice-render"}:
        if not (path.parent / "voice_edit.json").is_file():
            raise ValueError("请先在时间轴新增或编辑配音片段。")
        project["voice_edit_ids"] = [clip_id] if clip_id is not None else None
        project["voice_edit_force"] = command == "voice-generate"
        stages = ["voice_generate"] if command == "voice-generate" else ["separate", "voice_generate", "remix", "compose", "publish_text"]
        write_json(path, project)
    elif command == "cut-export":
        if not project.get("video"): raise ValueError("请先下载或导入视频。")
        stages = ["compose_source"]
        write_json(path, project)
    elif command == "remix":
        if not (path.parent / "dub_timing.json").is_file() and not (path.parent / "voice_edit.json").is_file():
            raise ValueError("此项目还没有生成配音，请先生成一次中文配音。")
        # Existing title/description remain valid; position edits require no model.
        stages = ["remix", "compose"]
        write_json(path, project)
    elif command == "resegment":
        if not project.get("audio"):
            raise ValueError("请先完成原音轨提取。")
        stages = ["resegment", "translate"]
    elif command == "retime":
        if "translate" not in project["completed"]:
            raise ValueError("请先完成识别和翻译。")
        stages = ["retime"]
    elif command == "render":
        if "translate" not in project["completed"]:
            raise ValueError("请先完成识别和翻译。")
        stages = ["separate", "tts", "compose", "publish_text"]
        if project.get("audio") and project.get("speech_layout_version") != 1:
            stages = ["resegment", "translate", *stages]
        project["active_sentence_alignment"] = project["settings"].get("sentence_alignment", True) and project["settings"].get("dub", True)
        write_json(path, project)
    elif command in {"prepare", "resume"}:
        stages = ["download", "extract", "transcribe", "translate"]
    elif command == "download":
        # Download-only uses a normal persisted project, but no AI dependencies.
        request = read_json(path)
        folder = Path(request["output_dir"]) / (datetime.now().strftime("%Y%m%d_%H%M%S_") + __import__("uuid").uuid4().hex[:6])
        folder.mkdir(parents=True)
        path = folder / "project.json"
        write_json(path, {"version": 1, "settings": request, "status": "created", "completed": []})
        emit("project", path=str(path))
        stages = ["download"]
    else:
        raise ValueError(command)
    for stage in stages:
        project = read_json(path)
        project.setdefault("completed", [])
        if stage in project["completed"] and command not in {"render", "retime", "resegment", "remix", "voice-generate", "voice-render", "cut-export"}:
            continue
        project["status"] = stage
        write_json(path, project)
        if stage == "tts" and command == "render" and project.get("active_sentence_alignment"):
            # Preserve the user's text and the generated voice; report overlap only.
            worker("sentence_prepare", path)
            worker("tts", path)
        else:
            worker(stage, path)
        if stage == "tts" and command == "render" and read_json(path).get("speech_layout_version") == 1 and read_json(path)["settings"].get("dub", True):
            worker("fit_speech_blocks", path)
        project = read_json(path)
        if stage not in project["completed"] and stage not in {"retime", "resegment"}:
            project["completed"].append(stage)
        write_json(path, project)
    project = read_json(path)
    if command == "download":
        project["status"] = "downloaded"
    elif command == "voice-generate":
        project["status"] = "voice_ready"
    elif command in {"render", "remix", "voice-render", "cut-export"}:
        project["status"] = "done"
    else:
        project["status"] = "review"
    write_json(path, project)
    emit(project["status"], path=str(path), message="处理完成")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["check", "prepare", "resume", "render", "remix", "cut-export", "voice-generate", "voice-render", "retime", "resegment", "download", "models", "check-login"])
    parser.add_argument("--clip-id", type=int)
    parser.add_argument("path", nargs="?")
    args = parser.parse_args()
    if args.command == "check":
        check()
    elif args.command == "check-login":
        check_login(args.path)
    elif args.command == "models":
        worker("models", args.path)
    else:
        execute(args.command, args.path, args.clip_id)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    try:
        main()
    except Exception as exc:
        emit("error", message=str(exc))
        if not isinstance(exc, WorkerFailure):
            traceback.print_exc(file=sys.stderr)
        sys.exit(1)
