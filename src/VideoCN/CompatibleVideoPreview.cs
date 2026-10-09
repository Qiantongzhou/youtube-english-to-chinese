using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoCN;

/// <summary>Local, disposable H.264/AAC preview; never changes the source or final export.</summary>
internal static class CompatibleVideoPreview
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    public static async Task<string> GetPlaybackPathAsync(string source, string projectFolder, double duration,
        IProgress<string> progress, CancellationToken cancellation)
    {
        var cached = CachePath(source, projectFolder);
        if (File.Exists(cached) && new FileInfo(cached).Length > 0) return cached;
        progress.Report("正在读取视频格式…");
        var probe = Path.Combine(Path.GetDirectoryName(FindFfmpeg())!, "ffprobe.exe");
        if (File.Exists(probe)) {
            var start = new ProcessStartInfo(probe) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name,pix_fmt", "-of", "json", source })
                start.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = start };
            using var job = ProcessJob.Start(process);
            using var registration = cancellation.Register(() => {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            });
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var json = await output; await errors;
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode == 0) {
                using var document = JsonDocument.Parse(json);
                var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                string Field(JsonElement stream, string name) => stream.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
                var visuals = streams.Where(s => Field(s, "codec_type") == "video").ToArray();
                if (visuals.Length == 0) throw new IOException("原文件没有视频轨道，请重新关联原视频。");
                var nativeVideo = Field(visuals[0], "codec_name") == "h264" && Field(visuals[0], "pix_fmt") == "yuv420p";
                var nativeAudio = streams.Where(s => Field(s, "codec_type") == "audio").All(s => Field(s, "codec_name") is "aac" or "mp3");
                if (nativeVideo && nativeAudio && Path.GetExtension(source).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) return source;
            }
        }
        return await GetAsync(source, projectFolder, duration, progress, cancellation);
    }

    private static string CachePath(string source, string projectFolder)
    {
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("原视频不存在，请重新关联原视频。", source);
        var fingerprint = $"preview-v1|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..20];
        return Path.Combine(projectFolder, ".preview", key + ".mp4");
    }

    public static async Task<string> GetAsync(string source, string projectFolder, double duration,
        IProgress<string> progress, CancellationToken cancellation)
    {
        var output = CachePath(source, projectFolder);
        var folder = Path.GetDirectoryName(output)!;
        var key = Path.GetFileNameWithoutExtension(output);
        Directory.CreateDirectory(folder);
        var gate = Gates.GetOrAdd(output, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        string? partial = null;
        try {
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(output) && new FileInfo(output).Length > 0) return output;
            var ffmpeg = FindFfmpeg();
            partial = Path.Combine(folder, key + "." + Guid.NewGuid().ToString("N") + ".partial.mp4");
            var codec = await ReadVideoCodecAsync(ffmpeg, source, cancellation);
            var decoder = codec switch {
                "av1" => "av1_cuvid", "h264" => "h264_cuvid", "hevc" => "hevc_cuvid",
                "vp9" => "vp9_cuvid", "vp8" => "vp8_cuvid", "mpeg2video" => "mpeg2_cuvid", _ => null
            };
            var attempts = decoder == null ? new[] { 1, 2 } : new[] { 0, 1, 2 };
            string error = "";
            foreach (var mode in attempts) {
                cancellation.ThrowIfCancellationRequested();
                var label = mode switch { 0 => "NVIDIA 硬件解码＋缩放＋编码", 1 => "CPU 多线程解码＋NVIDIA 编码", _ => "CPU 自动多线程" };
                progress.Report("正在准备兼容预览 · " + label);
                var result = await TranscodeAsync(ffmpeg, source, partial, decoder, mode, label, duration, progress, cancellation);
                if (result.ExitCode == 0 && File.Exists(partial) && new FileInfo(partial).Length > 0) {
                    LogAttempt(folder, label + "：完成");
                    File.Move(partial, output, overwrite: true);
                    partial = null;
                    return output;
                }
                error = result.Error;
                LogAttempt(folder, label + "：失败\n" + error);
                progress.Report(label + "不可用，正在切换兼容方案…");
            }
            throw new IOException("兼容预览生成失败：" + error.Trim());
        }
        finally {
            if (partial != null) { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            gate.Release();
        }
    }

    private static async Task<string> ReadVideoCodecAsync(string ffmpeg, string source, CancellationToken cancellation)
    {
        var probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        if (!File.Exists(probe)) return "";
        var start = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "default=noprint_wrappers=1:nokey=1", source })
            start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        using var job = ProcessJob.Start(process);
        using var registration = cancellation.Register(() => Kill(process));
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var codec = await output; await errors;
        cancellation.ThrowIfCancellationRequested();
        return process.ExitCode == 0 ? codec.Trim() : "";
    }

    private static async Task<(int ExitCode, string Error)> TranscodeAsync(string ffmpeg, string source, string partial,
        string? decoder, int mode, string label, double duration, IProgress<string> progress, CancellationToken cancellation)
    {
            var start = new ProcessStartInfo(ffmpeg) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            List<string> args = ["-nostdin", "-hide_banner", "-v", "error", "-y", "-filter_threads", "0", "-threads", "0"];
            if (mode == 0) args.AddRange(["-hwaccel", "cuda", "-hwaccel_device", "0", "-hwaccel_output_format", "cuda", "-c:v", decoder!]);
            args.AddRange(["-i", source, "-map", "0:v:0", "-map", "0:a:0?"]);
            if (mode == 0) {
                // Keep decoded frames on the GPU through resize and NVENC.
                args.AddRange(["-vf", "scale_cuda=w='trunc(min(1280,iw)/2)*2':h=-2:format=nv12:interp_algo=bilinear:passthrough=0,fps=30"]);
            } else {
                args.AddRange(["-vf", "fps=30,scale=w='trunc(min(1280,iw)/2)*2':h=-2:flags=fast_bilinear", "-pix_fmt", "yuv420p"]);
            }
            if (mode < 2) {
                args.AddRange(["-c:v", "h264_nvenc", "-gpu", "0", "-preset", "p1", "-rc", "vbr", "-cq", "26", "-b:v", "0", "-profile:v", "main"]);
            } else {
                args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-crf", "26", "-threads", "0"]);
            }
            args.AddRange(["-g", "60", "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart",
                "-progress", "pipe:1", "-nostats", partial]);
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = start };
            using var job = ProcessJob.Start(process);
            using var registration = cancellation.Register(() => Kill(process));
            var errorTask = process.StandardError.ReadToEndAsync();
            double seconds = 0;
            string speed = "", fps = "";
            while (await process.StandardOutput.ReadLineAsync() is { } line) {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out var us)) seconds = Math.Max(0, us / 1_000_000.0);
                else if (line.StartsWith("speed=", StringComparison.Ordinal)) speed = line[6..].Trim();
                else if (line.StartsWith("fps=", StringComparison.Ordinal)) fps = line[4..].Trim();
                else if (line.StartsWith("progress=", StringComparison.Ordinal)) {
                    var done = duration > 0 ? $"{Math.Clamp(seconds / duration * 100, 0, 99):F0}%" : $"已处理 {seconds:F0} 秒";
                    progress.Report($"兼容预览：{done} · {label} · {speed} · {fps} 帧/秒");
                }
            }
            await process.WaitForExitAsync();
            var error = await errorTask;
            cancellation.ThrowIfCancellationRequested();
            return (process.ExitCode, error.Length > 2000 ? error[^2000..] : error);
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }

    private static void LogAttempt(string folder, string message)
    {
        try { File.AppendAllText(Path.Combine(folder, "preview-transcode.log"), $"[{DateTime.Now:O}] {message}\n", Encoding.UTF8); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static string FindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("VIDEOCN_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) {
            var path = Path.Combine(configured, "tools", "ffmpeg.exe");
            if (File.Exists(path)) return path;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) {
            var path = Path.Combine(dir.FullName, "tools", "ffmpeg.exe");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("缺少 FFmpeg，请先在环境与模型页安装下载工具。");
    }
}
