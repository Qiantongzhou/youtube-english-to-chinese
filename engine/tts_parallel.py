"""Independent voice models share a sentence queue across up to two CUDA GPUs."""
import hashlib
import json
import multiprocessing
import queue
import time
from pathlib import Path

from core import emit
from speech_rate import units

VOICE_STYLE_VERSION = "consistent-v4"
VOICE_STYLE_SEED = 20260915
VOICE_INSTRUCT = (
    "整条视频保持同一位中文讲解员的播音状态。语气温和、稳定、清晰、自然，"
    "保持中等语速和稳定音高，不夸张表演，不突然提高或降低情绪，不拖长句尾；"
    "按中文标点做轻微停顿，专有名词和英文名保持平稳读法。"
)


def generation_options(consistency, temperature, top_p, repetition_penalty):
    # A fixed seed plus very low sampling keeps both workers reproducible while
    # still allowing the model to choose an end token on longer sentences.
    return dict(
        do_sample=True, top_k=50,
        top_p=0.90 if consistency else top_p,
        temperature=0.20 if consistency else temperature,
        repetition_penalty=repetition_penalty,
        subtalker_dosample=True, subtalker_top_k=50,
        subtalker_top_p=0.90 if consistency else top_p,
        subtalker_temperature=0.20 if consistency else temperature)


def token_limit(text):
    # Normal speech usually ends well before this allowance. The limit bounds
    # a sentence that fails to emit EOS without shortening ordinary sentences.
    return max(128, min(1024, units(text) * 6 + 96))


def style_signature(model_id, voice, text, style=VOICE_INSTRUCT, seed=VOICE_STYLE_SEED,
                    temperature=.65, top_p=.90, repetition_penalty=1.05, consistency=True):
    return hashlib.sha256(json.dumps(
        [model_id, voice, text, VOICE_STYLE_VERSION, style,
         style_seed(text, seed, consistency),
         generation_options(consistency, temperature, top_p, repetition_penalty)],
        ensure_ascii=False).encode()).hexdigest()[:24]


def style_seed(text, seed=VOICE_STYLE_SEED, consistency=True):
    # Consistency mode resets every sentence to the same seed on either worker.
    offset = 0 if consistency else int(hashlib.sha256(text.encode("utf-8")).hexdigest()[:8], 16)
    return (seed + offset) % (2**63)


def _generate(device, model_path, voice, style, options, jobs, events, log_path):
    # Spawn isolates CUDA state. Each process owns one model and one device.
    from contextlib import redirect_stdout, redirect_stderr
    task_id = None
    try:
        logical_device = device
        if device.startswith("cuda:"):
            # Isolate each worker to one physical adapter. Inside the child it
            # becomes logical cuda:0, so model libraries cannot silently pick
            # the parent's default GPU.
            physical_index = device.split(":", 1)[1]
            import os
            os.environ["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID"
            os.environ["CUDA_VISIBLE_DEVICES"] = physical_index
            logical_device = "cuda:0"
        with open(log_path, "w", encoding="utf-8", buffering=1) as log, redirect_stdout(log), redirect_stderr(log):
            events.put(("loading", device, None, "正在导入依赖并加载语音模型"))
            import soundfile as sf
            from worker import torch_setup
            torch = torch_setup()
            if logical_device.startswith("cuda:"):
                torch.cuda.set_device(0)
            from qwen_tts import Qwen3TTSModel
            model = Qwen3TTSModel.from_pretrained(
                model_path, device_map=logical_device,
                dtype=torch.bfloat16 if logical_device.startswith("cuda:") else torch.float32,
                attn_implementation="sdpa")
            model.model.eval()
            actual = str(next(model.model.parameters()).device)
            adapter = torch.cuda.get_device_name(0) if logical_device.startswith("cuda:") else "CPU"
            events.put(("ready", device, None, f"实际设备 {actual} · {adapter}"))
            while True:
                task = jobs.get()
                if task is None:
                    return
                task_id, text, destination, seed = task
                events.put(("started", device, task_id, ""))
                torch.manual_seed(seed)
                if logical_device.startswith("cuda:"):
                    torch.cuda.manual_seed(seed)
                steps, last_report = 0, time.monotonic()

                def report_step(module, inputs, output):
                    # Observe real talker forwards, without changing logits or sampling.
                    # Qwen's outer wrapper does not forward a streamer/stopping criteria.
                    nonlocal steps, last_report
                    steps += 1
                    now = time.monotonic()
                    if steps == 1 or now - last_report >= 3:
                        events.put(("generating", device, task_id, steps))
                        last_report = now

                hook = model.model.talker.register_forward_hook(report_step)
                try:
                    wavs, rate = model.generate_custom_voice(
                        text=text, language="Chinese", speaker=voice,
                        instruct=style, max_new_tokens=token_limit(text),
                        **options)
                finally:
                    hook.remove()
                if len(wavs) == 0 or len(wavs[0]) == 0:
                    raise RuntimeError("语音模型返回了空音频。")
                raw = Path(destination)
                partial = raw.with_suffix(".partial.wav")
                sf.write(str(partial), wavs[0], rate)
                partial.replace(raw)
                events.put(("done", device, task_id, ""))
    except Exception as exc:
        events.put(("error", device, task_id, str(exc)))


def generate(segments, cache, model_id, voice, device, progress, style=VOICE_INSTRUCT,
             seed=VOICE_STYLE_SEED, temperature=.65, top_p=.90, repetition_penalty=1.05,
             consistency=True):
    options = generation_options(consistency, temperature, top_p, repetition_penalty)
    emit("log", message=("配音一致性优先：两层均使用极低随机采样，双卡共享风格、参数和句子起始种子；每句限制 128–1024 推理步。"
                         if consistency else "配音随机采样：双卡共享风格与参数，使用按文本固定的随机种子。"))
    raw_paths, pending, multiplicity = [], {}, {}
    for segment in segments:
        signature = style_signature(model_id, voice, segment["zh"], style, seed,
                                    temperature, top_p, repetition_penalty, consistency)
        raw = (cache / (signature + ".wav")).resolve()
        raw_paths.append(raw)
        if raw.is_file() and raw.stat().st_size > 44:
            continue
        # Identical text shares one job, preventing competing cache writes.
        pending[signature] = (signature, segment["zh"], str(raw), style_seed(segment["zh"], seed, consistency))
        multiplicity[signature] = multiplicity.get(signature, 0) + 1
    completed = len(segments) - sum(multiplicity.values())
    progress("tts", f"已复用 {completed}/{len(segments)} 句配音缓存", completed/len(segments)*.9)
    if not pending:
        return raw_paths

    if device == "cuda":
        from worker import torch_setup
        torch = torch_setup()
        count = min(2, torch.cuda.device_count(), len(pending))
        if count == 0:
            raise RuntimeError("CUDA 不可用，请修复 GPU 环境，或在界面选择 CPU。")
        devices = [f"cuda:{i}" for i in range(count)]
        emit("log", message="配音设备：" + "；".join(
            f"GPU {i} · {torch.cuda.get_device_name(i)}" for i in range(count)))
    elif device == "cpu":
        devices = ["cpu"]
    else:
        raise ValueError(f"不支持的配音设备：{device}")

    # Resolve/download once, before either GPU loads the model.
    from huggingface_hub import snapshot_download
    progress("tts", "正在准备配音模型与 GPU 任务队列…")
    try:
        model_path = snapshot_download(model_id, local_files_only=True)
        if not all((Path(model_path)/name).is_file() for name in
                   ["model.safetensors", "speech_tokenizer/model.safetensors"]):
            raise FileNotFoundError("Incomplete voice model cache")
    except Exception:
        model_path = snapshot_download(model_id)

    context = multiprocessing.get_context("spawn")
    jobs, events = context.Queue(), context.Queue()
    processes, finished = [], set()
    worker_state = {}
    logs = cache.parent / "tts-workers"
    logs.mkdir(exist_ok=True)
    try:
        # Put short sentences first so initial completion is visible.
        # Then schedule the remaining long sentences first to balance the cards.
        ordered = sorted(pending.values(), key=lambda task: units(task[1]))
        ordered = ordered[:len(devices)] + list(reversed(ordered[len(devices):]))
        sentence_numbers = {task[0]: i + 1 for i, task in enumerate(ordered)}
        for task in ordered:
            jobs.put(task)
        for _ in devices:
            jobs.put(None)
        for target in devices:
            process = context.Process(target=_generate, args=(
                target, model_path, voice, style, options,
                jobs, events, str(logs / (target.replace(":", "-")+".log"))),
                name="VideoCN-TTS-"+target, daemon=True)
            process.start()
            processes.append(process)
            now = time.monotonic()
            worker_state[target] = {"phase": "loading", "last": now, "since": now}
        last_status = time.monotonic()
        while len(finished) < len(pending):
            now = time.monotonic()
            for target, state in worker_state.items():
                if state["phase"] == "idle":
                    continue
                timeout = (600 if state["phase"] == "loading" else 300) if target.startswith("cuda:") else 1800
                if now - state["last"] > timeout:
                    raise RuntimeError(
                        f"{target} {'加载模型' if state['phase'] == 'loading' else '生成配音'}"
                        f"超过 {timeout} 秒未返回实际进度，已停止等待；已完成的音频缓存保留。"
                        "请查看 tts-workers 日志，可继续任务重试。")
            if now - last_status >= 10:
                status = "；".join(
                    f"{target} {'加载模型' if state['phase'] == 'loading' else '处理中'}"
                    f"（已用 {int(now - state['since'])} 秒）"
                    for target, state in worker_state.items() if state["phase"] != "idle")
                progress("tts", f"已完成 {completed}/{len(segments)} 句 · {status}", completed/len(segments)*.9)
                last_status = now
            try:
                kind, target, task_id, message = events.get(timeout=1)
            except queue.Empty:
                failed = [p for p in processes if p.exitcode not in (None, 0)]
                if failed or not any(p.is_alive() for p in processes):
                    raise RuntimeError("配音子进程提前退出；已完成音频已缓存，详情见 tts-workers 日志。")
                continue
            if kind == "error":
                raise RuntimeError(f"{target} 配音失败：{message}。已完成音频已缓存，详情见 tts-workers 日志。")
            state = worker_state[target]
            state["last"] = time.monotonic()
            if kind == "loading":
                progress("tts", f"{target} {message}…", completed/len(segments)*.9)
            elif kind == "ready":
                state["phase"] = "idle"
                emit("log", message=f"{target} 模型已加载（{message}），开始领取配音任务。")
            elif kind == "started":
                state.update(phase="generating", since=time.monotonic())
                progress("tts", f"{target} 开始生成任务 {sentence_numbers[task_id]}/{len(pending)}"
                         f" · {units(pending[task_id][1])} 字/词 · 最多 {token_limit(pending[task_id][1])} 步",
                         completed/len(segments)*.9)
                emit("log", message=f"{target} 正在生成配音任务 {task_id[:8]}。")
            elif kind == "generating":
                progress("tts", f"已完成 {completed}/{len(segments)} 句 · {target} 当前句已推理 {message} 步"
                         f"（已用 {int(time.monotonic() - state['since'])} 秒）", completed/len(segments)*.9)
                last_status = time.monotonic()
            elif kind == "done" and task_id not in finished:
                state["phase"] = "idle"
                finished.add(task_id)
                completed += multiplicity[task_id]
                progress("tts", f"{target} 完成 · 中文配音 {completed}/{len(segments)} 句", completed/len(segments)*.9)
    finally:
        # Only this stage's own children are stopped; Windows Job Object also
        # covers them when the UI cancels or the application closes.
        for process in processes:
            if process.is_alive(): process.terminate()
        for process in processes:
            process.join(timeout=3)
            if process.is_alive():
                process.kill()
                process.join(timeout=3)
        for channel in (jobs, events):
            channel.cancel_join_thread()
            channel.close()
    return raw_paths
