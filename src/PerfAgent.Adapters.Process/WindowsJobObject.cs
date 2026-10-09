using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PerfAgent.Adapters.Process;

/// <summary>
/// Job Object de Windows con KILL_ON_JOB_CLOSE. Si el servidor perf-lab se asigna a sí mismo al job, todos los
/// procesos que lance después (API, mocks, k6, proveedores MCP) mueren con él aunque termine de forma abrupta:
/// el sistema operativo garantiza que no queden huérfanos.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeJobHandle _handle;

    private WindowsJobObject(SafeJobHandle handle) => _handle = handle;

    public static WindowsJobObject? TryCreateKillOnClose(out string? error)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            error = $"CreateJobObject falló ({Marshal.GetLastPInvokeError()}).";
            handle.Dispose();
            return null;
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            error = $"SetInformationJobObject falló ({Marshal.GetLastPInvokeError()}).";
            handle.Dispose();
            return null;
        }

        error = null;
        return new WindowsJobObject(handle);
    }

    public bool TryAssignCurrentProcess(out string? error)
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        return TryAssign(current.Handle, out error);
    }

    public bool TryAssign(System.Diagnostics.Process process, out string? error) => TryAssign(process.Handle, out error);

    private bool TryAssign(IntPtr processHandle, out string? error)
    {
        if (AssignProcessToJobObject(_handle, processHandle))
        {
            error = null;
            return true;
        }

        error = $"AssignProcessToJobObject falló ({Marshal.GetLastPInvokeError()}).";
        return false;
    }

    public void Dispose() => _handle.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
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
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
