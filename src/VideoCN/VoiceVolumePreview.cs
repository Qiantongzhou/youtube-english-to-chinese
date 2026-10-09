using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VideoCN;

/// <summary>Boost only the disposable preview; preserve fitted clips and TTS caches.</summary>
internal static class VoiceVolumePreview
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static readonly SemaphoreSlim Workers = new(2);

    public static async Task<string> GetAsync(string source, string folder, double gain, CancellationToken cancellation)
    {
        if (gain <= 1) return source;
        var info = new FileInfo(source);
        var amount = gain.ToString("0.00", CultureInfo.InvariantCulture);
        var fingerprint = $"voice-gain-v1|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{amount}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..24];
        var destination = Path.Combine(folder, ".preview", "voice-volume", key + ".wav");
        var gate = Gates.GetOrAdd(destination, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        try {
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(destination) && new FileInfo(destination).Length > 44) return destination;
            await Workers.WaitAsync(cancellation);
            string? partial = null;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial.wav";
                var start = new ProcessStartInfo(CompatibleVideoPreview.FindFfmpeg()) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
                };
                foreach (var arg in new[] { "-hide_banner", "-nostdin", "-v", "error", "-y", "-i", source,
                    "-vn", "-af", $"volume={amount},alimiter=limit=0.95:level=false:latency=true",
                    "-c:a", "pcm_s16le", partial }) start.ArgumentList.Add(arg);
                using var process = new Process { StartInfo = start };
                using var job = ProcessJob.Start(process);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var registration = timeout.Token.Register(() => {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                });
                var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var message = await errors;
                cancellation.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested) throw new TimeoutException("人声音量预览准备超时，请重试。");
                if (process.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length <= 44)
                    throw new IOException("人声音量预览准备失败：" + message.Trim());
                File.Move(partial, destination, overwrite: true); partial = null;
                return destination;
            }
            finally {
                if (partial != null) { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                Workers.Release();
            }
        }
        finally { gate.Release(); }
    }
}
