using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace OpenClawTray.Services;

/// <summary>
/// Opens a dashboard URL with the shell and returns a browser process id when Windows provides one.
/// Launch failures are not logged: the exception text can contain the credential URL.
/// </summary>
internal static class DashboardBrowserShell
{
    internal static bool TryOpen(string url, out int? processId)
    {
        processId = null;
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return InterpretStartedProcess(process, out processId);
        }
        catch (Exception)
        {
            processId = null;
            return false;
        }
        finally
        {
            try
            {
                process?.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Dashboard browser handle release failed: {ex.GetType().Name}");
            }
        }
    }

    internal static bool InterpretStartedProcess(Process? process, out int? processId)
    {
        processId = null;
        if (process is null)
            return true;

        processId = TryReadProcessId(process);
        return true;
    }

    internal static string? ProcessNameFromOpenCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var trimmed = command.Trim();
        string path;
        if (trimmed.StartsWith('\"'))
        {
            var end = trimmed.IndexOf('\"', 1);
            if (end <= 1)
                return null;
            path = trimmed[1..end];
        }
        else
        {
            var space = trimmed.IndexOf(' ');
            path = space < 0 ? trimmed : trimmed[..space];
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    [SupportedOSPlatform("windows")]
    internal static string? TryGetDefaultBrowserProcessName()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            var progId = choice?.GetValue("ProgId") as string;
            if (string.IsNullOrWhiteSpace(progId))
                return null;

            using var commandKey = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
            return ProcessNameFromOpenCommand(commandKey?.GetValue(null) as string);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? TryReadProcessId(Process process)
    {
        try
        {
            if (process.HasExited)
                return null;
            return process.Id > 0 ? process.Id : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}
