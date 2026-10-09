using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VideoCN;

// Windows owns the lifetime of all child processes, including CUDA model runners.
// Closing or crashing the GUI must not leave models running in the background.
internal sealed class ProcessJob : IDisposable
{
    private static readonly object RegistryLock = new();
    private static readonly HashSet<ProcessJob> Jobs = [];
    private static bool shuttingDown;
    private readonly SafeFileHandle handle;
    private bool disposed;

    private ProcessJob(SafeFileHandle handle) => this.handle = handle;

    public static ProcessJob Start(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (RegistryLock) {
            if (shuttingDown) throw new OperationCanceledException("VideoCN 正在退出，已停止启动后台任务。");
            SafeFileHandle? jobHandle = null;
            try {
                // Configure the job before starting a helper. Shutdown cannot
                // race with process startup, assignment, or registry insertion.
                jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (jobHandle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var info = new ExtendedLimits();
                info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                var size = Marshal.SizeOf<ExtendedLimits>();
                var pointer = Marshal.AllocHGlobal(size);
                try {
                    Marshal.StructureToPtr(info, pointer, false);
                    if (!SetInformationJobObject(jobHandle, 9, pointer, (uint)size))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                } finally { Marshal.FreeHGlobal(pointer); }
                if (!process.Start()) throw new InvalidOperationException("未能启动后台任务。");
                if (!AssignProcessToJobObject(jobHandle, process.Handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var job = new ProcessJob(jobHandle);
                Jobs.Add(job);
                return job;
            }
            catch {
                // Even a partially successful Start must not leak a helper.
                // Cleanup failures must not replace the original startup error.
                try { process.Kill(entireProcessTree: true); } catch { }
                try { jobHandle?.Dispose(); } catch { }
                throw;
            }
        }
    }

    public static void ShutdownAll()
    {
        lock (RegistryLock) {
            shuttingDown = true;
            foreach (var job in Jobs.ToArray()) job.Dispose();
        }
    }

    public void Dispose()
    {
        lock (RegistryLock) {
            if (disposed) return;
            disposed = true;
            Jobs.Remove(this);
            handle.Dispose();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
