using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Owns one isolated OpenClaw process for the accessibility test collection.
/// Navigation is sent through the same deep-link IPC path used by installed apps.
/// </summary>
public sealed class AccessibilityAppFixture : IDisposable
{
    private const int ShowMaximized = 3;
    private const int VirtualScreenLeft = 76;
    private const int VirtualScreenTop = 77;
    private const int VirtualScreenWidth = 78;
    private const int VirtualScreenHeight = 79;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DeepLinkTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NavigationSettleTime = TimeSpan.FromMilliseconds(1_000);
    private static readonly TimeSpan WindowsGraphicsCaptureFrameTimeout = TimeSpan.FromSeconds(5);

    // Screenshot capture method names, in selection order. These are the
    // only values LastScreenshotCaptureMethod may report on success, and the
    // only values that ever appear in the redacted, publishable proof.txt.
    private const string WindowsGraphicsCaptureMethod = "WindowsGraphicsCapture";
    private const string PrintWindowCaptureMethod = "PrintWindow";
    private const string CopyFromScreenCaptureMethod = "CopyFromScreen";

    private readonly string _dataDirectory;
    private readonly string _executablePath;
    private readonly Process _process;

    public IntPtr HubWindowHandle { get; }

    public string? LastScreenshotCaptureMethod { get; private set; }

    /// <summary>
    /// Method-specific diagnostic detail (exception type, HResult, message)
    /// for every capture attempt, successful or not. Intended for private
    /// test output (xunit ITestOutputHelper / TRX) only; never written to
    /// the redacted, publishable proof.txt.
    /// </summary>
    public IReadOnlyList<string> LastScreenshotDiagnostics { get; private set; } = Array.Empty<string>();

    public AccessibilityAppFixture()
    {
        _executablePath = Path.Combine(AppContext.BaseDirectory, "OpenClaw.Tray.WinUI.exe");
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException(
                "The real tray executable was not copied beside the UI test assembly.",
                _executablePath);
        }

        _dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"OpenClaw.Tray.Axe.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        File.WriteAllText(
            Path.Combine(_dataDirectory, "settings.json"),
            """
            {
              "SettingsSchemaVersion": 1,
              "EnableMcpServer": true,
              "GlobalHotkeyEnabled": false,
              "AutoStart": false
            }
            """);

        _process = StartProcess($"{OpenClawTray.AppIdentity.ProtocolScheme}://hub/connection");
        HubWindowHandle = WaitForHubWindow();
        AxeHelper.Initialize(_process.Id);
    }

    public async Task NavigateAsync(string pageTag, string pageMarkerAutomationId)
    {
        EnsureTargetIsAlive();

        using var sender = StartProcess($"{OpenClawTray.AppIdentity.ProtocolScheme}://hub/{pageTag}");
        using var timeout = new CancellationTokenSource(DeepLinkTimeout);
        try
        {
            await sender.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!sender.HasExited)
                sender.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"Timed out forwarding the '{pageTag}' deep link to the accessibility app.");
        }

        EnsureTargetIsAlive();
        await WaitForPageMarkerAsync(pageTag, pageMarkerAutomationId);
    }

    /// <summary>
    /// Captures a screenshot witness of the Hub window, trying methods in
    /// order of increasing risk: Windows.Graphics.Capture app-window capture
    /// first (compositor output, works even without foreground focus and
    /// without capturing anything else on the desktop), then app-scoped
    /// PrintWindow, then a full screen-copy fallback that is only attempted
    /// after proving the Hub window is visible, not minimized/cloaked, fully
    /// on-screen, the foreground window, and unobscured by any other visible
    /// window in front of it. This never captures the full desktop or an
    /// unproven application rectangle, and always rejects a blank/near-
    /// uniform result via <see cref="HasMeaningfulVisualContent"/>.
    /// </summary>
    public string? CaptureHubScreenshotIfRequested()
    {
        var configuredPath = Environment.GetEnvironmentVariable("OPENCLAW_UI_SCREENSHOT_PATH");
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        EnsureTargetIsAlive();
        var bounds = AutomationElement.FromHandle(HubWindowHandle).Current.BoundingRectangle;
        var screenLeft = GetSystemMetrics(VirtualScreenLeft);
        var screenTop = GetSystemMetrics(VirtualScreenTop);
        var screenRight = screenLeft + GetSystemMetrics(VirtualScreenWidth);
        var screenBottom = screenTop + GetSystemMetrics(VirtualScreenHeight);
        var left = Math.Max(screenLeft, (int)Math.Floor(bounds.Left));
        var top = Math.Max(screenTop, (int)Math.Floor(bounds.Top));
        var right = Math.Min(screenRight, (int)Math.Ceiling(bounds.Right));
        var bottom = Math.Min(screenBottom, (int)Math.Ceiling(bounds.Bottom));
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Hub screenshot bounds were invalid: {width}x{height}.");

        var path = Path.GetFullPath(configuredPath, Environment.CurrentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var diagnostics = new List<string>();
        LastScreenshotCaptureMethod = null;
        LastScreenshotDiagnostics = diagnostics;

        var attempts = new[]
        {
            new CaptureMethodSelector.MethodAttempt(WindowsGraphicsCaptureMethod, () => TryWindowsGraphicsCapture(diagnostics)),
            new CaptureMethodSelector.MethodAttempt(PrintWindowCaptureMethod, () => TryPrintWindow(width, height, diagnostics)),
            new CaptureMethodSelector.MethodAttempt(CopyFromScreenCaptureMethod, () => TrySafeForegroundScreenCopy(width, height, left, top, diagnostics)),
        };

        var (method, bitmap) = CaptureMethodSelector.SelectFirstMeaningful(attempts, HasMeaningfulVisualContent, diagnostics);
        using (bitmap)
        {
            bitmap.Save(path, ImageFormat.Png);
        }
        LastScreenshotCaptureMethod = method;

        if (new FileInfo(path).Length == 0)
            throw new InvalidOperationException("Hub screenshot capture produced an empty file.");
        return path;
    }

    /// <summary>
    /// First attempt: picker-free Windows.Graphics.Capture of the Hub HWND
    /// via <see cref="WindowGraphicsCaptureHelper"/>. Reads compositor output
    /// directly, so it does not require foreground focus or an unobscured
    /// window, and never touches any other window or the desktop. Returns
    /// null (never throws) on any failure, recording method-specific
    /// exception type/HResult/message into <paramref name="diagnostics"/>.
    /// </summary>
    private Bitmap? TryWindowsGraphicsCapture(List<string> diagnostics)
    {
        WindowGraphicsCaptureHelper.CaptureOutcome outcome;
        try
        {
            outcome = WindowGraphicsCaptureHelper.TryCaptureWindow(HubWindowHandle, WindowsGraphicsCaptureFrameTimeout);
        }
        catch (Exception ex)
        {
            diagnostics.Add($"{WindowsGraphicsCaptureMethod}: {ex.GetType().Name} hresult=0x{ex.HResult:X8} message={ex.Message}");
            return null;
        }

        if (!outcome.Success)
        {
            var hresult = outcome.HResult is { } hr ? $"0x{hr:X8}" : "n/a";
            diagnostics.Add($"{WindowsGraphicsCaptureMethod}: {outcome.FailureKind} hresult={hresult} message={outcome.FailureMessage}");
            return null;
        }

        using var stream = new MemoryStream(outcome.PngBytes!);
        using var loaded = new Bitmap(stream);
        return new Bitmap(loaded); // detach the copy from the backing stream before it is disposed
    }

    private Bitmap? TryPrintWindow(int width, int height, List<string> diagnostics)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        var deviceContext = graphics.GetHdc();
        bool ok;
        try
        {
            const uint renderFullContent = 2;
            ok = PrintWindow(HubWindowHandle, deviceContext, renderFullContent);
        }
        finally
        {
            graphics.ReleaseHdc(deviceContext);
        }

        if (ok)
            return bitmap;

        bitmap.Dispose();
        diagnostics.Add($"{PrintWindowCaptureMethod}: PrintWindow returned false");
        return null;
    }

    /// <summary>
    /// Last-resort attempt: only captures the Hub's own screen rectangle,
    /// and only after proving the window is visible, not minimized or
    /// cloaked, fully on-screen, the foreground window, and unobscured by
    /// any other visible window above it in z-order. Never captures the
    /// full desktop and never proceeds on an unproven rectangle.
    /// </summary>
    private Bitmap? TrySafeForegroundScreenCopy(int width, int height, int left, int top, List<string> diagnostics)
    {
        var foreground = false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (TryForegroundHubWindow())
            {
                foreground = true;
                break;
            }
            Thread.Sleep(100);
        }
        if (!foreground)
        {
            diagnostics.Add($"{CopyFromScreenCaptureMethod}: could not foreground the Hub window");
            return null;
        }
        Thread.Sleep(500);

        if (!TryVerifyHubWindowSafeToScreenCopy(out var reason))
        {
            diagnostics.Add($"{CopyFromScreenCaptureMethod}: refused, {reason}");
            return null;
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(
            left,
            top,
            0,
            0,
            bitmap.Size,
            CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private bool TryForegroundHubWindow()
    {
        var currentThreadId = GetCurrentThreadId();
        var foregroundThreadId = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var targetThreadId = GetWindowThreadProcessId(HubWindowHandle, out _);
        var attachedToForeground = false;
        var attachedToTarget = false;
        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                attachedToForeground = AttachThreadInput(currentThreadId, foregroundThreadId, attach: true);
            if (targetThreadId != 0 && targetThreadId != currentThreadId)
                attachedToTarget = AttachThreadInput(currentThreadId, targetThreadId, attach: true);

            _ = ShowWindow(HubWindowHandle, ShowMaximized);
            _ = BringWindowToTop(HubWindowHandle);
            _ = SetForegroundWindow(HubWindowHandle);
            return GetForegroundWindow() == HubWindowHandle;
        }
        finally
        {
            if (attachedToTarget)
                _ = AttachThreadInput(currentThreadId, targetThreadId, attach: false);
            if (attachedToForeground)
                _ = AttachThreadInput(currentThreadId, foregroundThreadId, attach: false);
        }
    }

    /// <summary>
    /// Proves every safety condition the CopyFromScreen fallback requires:
    /// the Hub window is visible, not minimized, not DWM-cloaked, fully
    /// within the virtual screen bounds, the current foreground window, and
    /// not covered by any other visible, non-cloaked, non-owned window
    /// higher in z-order. <see cref="EnumWindows"/> visits top-level windows
    /// top-to-bottom in z-order, so any window enumerated before the Hub
    /// handle is stacked above it.
    /// </summary>
    private bool TryVerifyHubWindowSafeToScreenCopy(out string reason)
    {
        if (!IsWindowVisible(HubWindowHandle))
        {
            reason = "Hub window is not visible";
            return false;
        }
        if (IsIconic(HubWindowHandle))
        {
            reason = "Hub window is minimized";
            return false;
        }
        if (IsWindowCloaked(HubWindowHandle))
        {
            reason = "Hub window is DWM-cloaked";
            return false;
        }
        if (!GetWindowRect(HubWindowHandle, out var hubRect))
        {
            reason = "GetWindowRect failed for the Hub window";
            return false;
        }

        var screenLeft = GetSystemMetrics(VirtualScreenLeft);
        var screenTop = GetSystemMetrics(VirtualScreenTop);
        var screenRight = screenLeft + GetSystemMetrics(VirtualScreenWidth);
        var screenBottom = screenTop + GetSystemMetrics(VirtualScreenHeight);
        if (hubRect.Left < screenLeft || hubRect.Top < screenTop ||
            hubRect.Right > screenRight || hubRect.Bottom > screenBottom)
        {
            reason = "Hub window is not fully on-screen";
            return false;
        }

        if (GetForegroundWindow() != HubWindowHandle)
        {
            reason = "Hub window is not the foreground window";
            return false;
        }

        var reachedHub = false;
        string? obscuredBy = null;
        EnumWindows((hWnd, _) =>
        {
            if (hWnd == HubWindowHandle)
            {
                reachedHub = true;
                return false; // reached the Hub window in z-order; nothing above it remains unchecked
            }
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd) || IsWindowCloaked(hWnd))
                return true;
            if (GetWindow(hWnd, GwOwner) == HubWindowHandle)
                return true; // window owned by the Hub (e.g. its own popups)
            if (!GetWindowRect(hWnd, out var otherRect))
                return true;
            if (RectsIntersect(hubRect, otherRect))
            {
                obscuredBy = $"hwnd=0x{hWnd:X}";
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (obscuredBy != null)
        {
            reason = $"Hub window is obscured by another visible window ({obscuredBy})";
            return false;
        }

        // Fail closed if enumeration never reached the Hub handle: the
        // unobscured claim above is only proven for windows actually visited
        // before the Hub in z-order, so an enumeration that never reaches it
        // (unexpected termination, or the Hub is not a top-level window)
        // cannot be trusted to have checked anything.
        if (!reachedHub)
        {
            reason = "z-order enumeration never reached the Hub window; unobscured z-order could not be proven";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        var hr = DwmGetWindowAttribute(hWnd, DwmwaCloaked, out var cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private static bool RectsIntersect(RECT a, RECT b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    internal static bool HasMeaningfulVisualContent(Bitmap bitmap)
    {
        // Ignore window chrome so title-bar controls cannot make an otherwise
        // blank client area look like meaningful application content.
        var left = bitmap.Width / 20;
        var top = bitmap.Height / 8;
        var right = bitmap.Width - left;
        var bottom = bitmap.Height - (bitmap.Height / 20);
        var stepX = Math.Max(1, (right - left) / 128);
        var stepY = Math.Max(1, (bottom - top) / 128);
        var sampledColors = new Dictionary<int, int>();
        var sampleCount = 0;
        for (var y = top; y < bottom; y += stepY)
        {
            for (var x = left; x < right; x += stepX)
            {
                var rgb = bitmap.GetPixel(x, y).ToArgb() & 0x00FFFFFF;
                sampledColors[rgb] = sampledColors.GetValueOrDefault(rgb) + 1;
                sampleCount++;
            }
        }
        if (sampledColors.Count < 3 || sampleCount == 0)
            return false;

        var dominantColorSamples = sampledColors.Values.Max();
        return dominantColorSamples < sampleCount * 0.997;
    }

    private async Task WaitForPageMarkerAsync(string pageTag, string automationId)
    {
        var stopwatch = Stopwatch.StartNew();
        var condition = new PropertyCondition(
            AutomationElement.AutomationIdProperty,
            automationId);

        while (stopwatch.Elapsed < NavigationTimeout)
        {
            EnsureTargetIsAlive();
            var hub = AutomationElement.FromHandle(HubWindowHandle);
            if (hub.FindFirst(TreeScope.Descendants, condition) != null)
                return;

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"The '{pageTag}' page did not expose its '{automationId}' marker " +
            $"within {NavigationTimeout.TotalSeconds:0} seconds.");
    }

    private Process StartProcess(string deepLink)
    {
        var startInfo = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add(deepLink);
        startInfo.Environment["OPENCLAW_TRAY_DATA_DIR"] = _dataDirectory;
        startInfo.Environment["OPENCLAW_SKIP_UPDATE_CHECK"] = "1";
        startInfo.Environment["OPENCLAW_FORCE_ONBOARDING"] = "0";
        startInfo.Environment["OPENCLAW_LANGUAGE"] = "en-US";
        startInfo.Environment["OPENCLAW_ACCESSIBILITY_TEST_CHAT"] = "1";
        startInfo.Environment["OPENCLAW_ACCESSIBILITY_TEST_SESSIONS"] = "1";

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the OpenClaw tray executable.");
    }

    private IntPtr WaitForHubWindow()
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StartupTimeout)
        {
            EnsureTargetIsAlive();
            _process.Refresh();
            if (_process.MainWindowHandle != IntPtr.Zero)
            {
                Thread.Sleep(NavigationSettleTime);
                EnsureTargetIsAlive();
                _process.Refresh();
                if (_process.MainWindowHandle != IntPtr.Zero)
                {
                    _ = ShowWindow(_process.MainWindowHandle, ShowMaximized);
                    Thread.Sleep(NavigationSettleTime);
                    return _process.MainWindowHandle;
                }
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException(
            $"OpenClaw did not expose its Hub window within {StartupTimeout.TotalSeconds:0} seconds.");
    }

    private void EnsureTargetIsAlive()
    {
        if (!_process.HasExited)
            return;

        var crashLogPath = Path.Combine(_dataDirectory, "crash.log");
        var crashLog = File.Exists(crashLogPath)
            ? $" Crash log: {File.ReadAllText(crashLogPath)}"
            : string.Empty;
        throw new InvalidOperationException(
            $"OpenClaw exited unexpectedly with code {_process.ExitCode}.{crashLog}");
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5_000);
        }
        _process.Dispose();

        // slopwatch-ignore: SW003 Test-owned temporary data cleanup is best-effort after process teardown.
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("user32.dll")]
    private static extern int ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr windowHandle, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    // Safety-gate P/Invokes for the CopyFromScreen fallback: these prove the
    // Hub window is visible, not minimized/cloaked, fully on-screen, and
    // unobscured before any pixel is ever read off the full screen.

    private const uint GwOwner = 4;
    private const int DwmwaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
