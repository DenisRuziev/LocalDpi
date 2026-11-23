using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LocalDpi;

internal static class ProcessJob
{
    internal static SafeFileHandle Create()
    {
        var limits = new byte[144]; // JOBOBJECT_EXTENDED_LIMIT_INFORMATION, Windows x64
        BitConverter.GetBytes(0x2000).CopyTo(limits, 16); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        var handle = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (!handle.IsInvalid && SetInformationJobObject(handle, 9, limits, limits.Length)) return handle;
        var error = new Win32Exception();
        handle.Dispose();
        throw error;
    }

    internal static void Attach(SafeFileHandle job, Process process)
    {
        if (!AssignProcessToJobObject(job, process.SafeHandle) && !process.HasExited)
            throw new Win32Exception();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, byte[] info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
