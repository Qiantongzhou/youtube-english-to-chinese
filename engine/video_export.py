"""GPU-first final MP4 export, retaining CPU subtitle rendering and audio mixing."""
from core import emit, probe, run, tool


def encode(project, folder, output, duration, audio_args, filters, progress, kept=None, timeline_duration=None):
    video = project["video"]
    streams = probe(video).get("streams", [])
    source = next((s for s in streams if s.get("codec_type") == "video"), {})
    decoder = {"av1": "av1_cuvid", "h264": "h264_cuvid", "hevc": "hevc_cuvid",
               "vp9": "vp9_cuvid", "vp8": "vp8_cuvid", "mpeg2video": "mpeg2_cuvid"}.get(source.get("codec_name"))
    transfer_format = {"yuv420p": "nv12", "nv12": "nv12", "yuv420p10le": "p010le", "p010le": "p010le"}.get(source.get("pix_fmt"))
    modes = ["gpu_decode", "gpu_encode", "cpu"] if decoder and transfer_format else ["gpu_encode", "cpu"]
    labels = {"gpu_decode": "NVIDIA GPU 0 解码＋编码", "gpu_encode": "CPU 解码＋NVIDIA GPU 0 编码", "cpu": "CPU 多线程编码"}
    for mode in modes:
        label = labels[mode]
        args = [tool("ffmpeg"), "-nostdin", "-hide_banner", "-v", "error", "-y", "-threads", "0"]
        video_filters = list(filters)
        if mode == "gpu_decode":
            args += ["-hwaccel", "cuda", "-hwaccel_device", "0", "-hwaccel_output_format", "cuda", "-c:v", decoder]
            # ASS and last-frame padding are CPU filters. Transfer explicitly so
            # FFmpeg never tries to feed CUDA surfaces into a software filter.
            video_filters = ["hwdownload", f"format={transfer_format}", "format=yuv420p", *video_filters]
        args += ["-i", video]
        if kept is not None:
            from video_edit import export_graph
            inputs, graph = export_graph(audio_args, video_filters, kept, timeline_duration,
                                         any(s.get("codec_type") == "audio" for s in streams))
            # FFmpeg's /option syntax loads the value from a file, avoiding Windows
            # command-line limits. The old filter_complex_script option was removed.
            graph_path = folder / "video-cut-filter.txt"
            graph_path.write_text(graph, encoding="utf-8")
            args += [*inputs, "-/filter_complex", str(graph_path), "-map", "[cut_video]", "-map", "[cut_audio]", "-fps_mode", "vfr"]
        else:
            args += audio_args
            if video_filters: args += ["-vf", ",".join(video_filters)]
        if mode == "cpu":
            args += ["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-threads", "0"]
        else:
            args += ["-c:v", "h264_nvenc", "-gpu", "0", "-preset", "p3", "-rc", "vbr", "-cq", "19", "-b:v", "0", "-profile:v", "high"]
        args += ["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-t", str(duration),
                 "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", str(output)]
        state = {"seconds": 0, "fps": "", "speed": ""}

        def tick(line):
            key, _, value = line.partition("=")
            if key == "out_time_us":
                try: state["seconds"] = max(0, int(value) / 1e6)
                except ValueError: pass
            elif key in {"fps", "speed"}: state[key] = value.strip()
            elif key == "progress":
                progress("compose", f"正在导出 · {label} · {state['speed']} · {state['fps']} 帧/秒",
                         min(1, state["seconds"] / duration))

        progress("compose", f"正在导出 · {label}…")
        emit("log", message=f"成品导出方案：{label}；保留原视频分辨率，字幕烧录与混音在 CPU 处理。")
        try:
            run(args, cwd=folder, on_line=tick)
        except RuntimeError as exc:
            if mode == "cpu": raise
            emit("log", message=f"{label}未完成，将回退重试。\n{str(exc)[-2400:]}")
            continue
        project["export_encoder"] = "libx264" if mode == "cpu" else "h264_nvenc"
        project["export_acceleration"] = mode
        return
