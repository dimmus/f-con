using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FCon.Core.Engine;

/// <summary>
/// Ties child processes to this one's lifetime using a Windows job object.
/// </summary>
/// <remarks>
/// A proxy core started as an ordinary child outlives its parent. If FCon is killed
/// rather than closed - a crash, Task Manager, or a force-kill from a build script -
/// the core keeps running, keeps the tunnel up and keeps its ports bound, which then
/// blocks the next start. Cleanup in a finally block cannot help: TerminateProcess
/// runs no user code at all.
///
/// A job object with KILL_ON_JOB_CLOSE moves the guarantee into the kernel. The
/// handle closes when this process ends however it ends, and Windows terminates
/// everything in the job.
/// </remarks>
public sealed partial class ProcessJob : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const uint LimitKillOnJobClose = 0x2000;

    private nint _handle;

    public ProcessJob()
    {
        _handle = CreateJobObject(nint.Zero, null);
        if (_handle == nint.Zero)
        {
            // Not fatal: without a job we simply fall back to killing children by hand.
            return;
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = LimitKillOnJobClose,
            },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(_handle, ExtendedLimitInformation, buffer, (uint)size))
            {
                CloseHandle(_handle);
                _handle = nint.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>True when the kernel is enforcing the lifetime link.</summary>
    public bool IsActive => _handle != nint.Zero;

    /// <summary>
    /// Put a process under the job. Returns false when the guarantee could not be
    /// established, so the caller knows it still owns cleanup itself.
    /// </summary>
    public bool Assign(Process process)
    {
        if (_handle == nint.Zero) return false;

        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // The process exited between starting and being assigned.
            return false;
        }
    }

    public void Dispose()
    {
        if (_handle == nint.Zero) return;
        CloseHandle(_handle);
        _handle = nint.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial nint CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
