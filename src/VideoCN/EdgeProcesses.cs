using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VideoCN;

internal static class EdgeProcesses
{
    internal static async Task<int> StopBackgroundAsync()
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName("msedge");
        try {
            var targets = processes.Where(p => !p.HasExited && p.SessionId == current.SessionId).ToArray();
            var ids = targets.Select(p => p.Id).ToHashSet();
            bool visible = false;
            EnumWindows((window, _) => {
                GetWindowThreadProcessId(window, out var pid);
                if (ids.Contains((int)pid) && IsWindowVisible(window)) visible = true;
                return true;
            }, IntPtr.Zero);
            if (visible) throw new InvalidOperationException("Edge 还有可见窗口。请先保存页面内容并关闭所有 Edge 窗口，再点「结束 Edge 后台并重试」。");
            foreach (var process in targets) {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { /* Another targeted process may already have closed it. */ }
                catch (Win32Exception ex) { if (!process.HasExited) throw new InvalidOperationException("无法结束某个 Edge 后台进程，请在任务管理器中手动结束 Microsoft Edge。", ex); }
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await Task.WhenAll(targets.Select(p => p.WaitForExitAsync(timeout.Token))); }
            catch (OperationCanceledException) { throw new InvalidOperationException("部分 Edge 后台进程尚未退出，请稍后重试。"); }
            return targets.Length;
        } finally { foreach (var process in processes) process.Dispose(); }
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
