using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Fixture-only check that a loopback browser-control port is owned by the
/// process that launched this app. Ordinary runs fail closed.
/// </summary>
public static class FixtureLoopbackListenerOwner
{
    public static bool IsOwnedByCurrentProcessParent(int port)
    {
        if (!OperatingSystem.IsWindows() || port is < 1 or > 65535)
            return false;
        try
        {
            if (!GatewayFixtureIsolation.IsEnabled)
                return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        using var current = Process.GetCurrentProcess();
        int parentId;
        try
        {
            parentId = GetParentProcessId(current.SafeHandle);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        if (parentId <= 0)
            return false;

        var snapshot = WindowsTcpListenerSnapshot.Capture();
        if (!snapshot.Ipv4Complete)
            return false;
        return snapshot.Listeners.Any(listener =>
            listener.Port == port &&
            listener.ProcessId == parentId &&
            listener.Address.Equals(IPAddress.Loopback));
    }

    private static int GetParentProcessId(SafeProcessHandle process)
    {
        var status = NtQueryInformationProcess(
            process, 0, out var basic, (uint)Marshal.SizeOf<ProcessBasicInformation>(), out var returned);
        if (status != 0 || returned != (uint)Marshal.SizeOf<ProcessBasicInformation>())
            throw new InvalidOperationException("The process parent could not be verified.");
        return checked((int)basic.ParentProcessId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public int ExitStatus;
        public IntPtr PebAddress;
        public nuint AffinityMask;
        public int BasePriority;
        public nuint ProcessId;
        public nuint ParentProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle process,
        int informationClass,
        out ProcessBasicInformation information,
        uint length,
        out uint returned);
}
