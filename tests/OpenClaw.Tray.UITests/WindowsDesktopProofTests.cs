using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Drives one deterministic, visible Hub route through the same isolated app
/// process and deep-link IPC path used by <see cref="AccessibilityScanTests"/>,
/// then records proof text and (when requested) a screenshot artifact.
///
/// This is the app-side half of the desktop proof contract consumed by
/// <c>scripts\capture-windows-desktop-proof.ps1</c>. The script is responsible
/// for setting <c>OPENCLAW_UI_SCREENSHOT_PATH</c> / <c>OPENCLAW_UI_PROOF_PATH</c>,
/// verifying the resulting artifacts, and failing closed. This test only
/// produces them when asked; it does not fail when the env vars are unset so
/// it stays safe to run in the normal Accessibility CI lane.
///
/// Screenshot capture is a witness only, never the pass/fail oracle: the
/// deterministic UI automation assertion (<see cref="AccessibilityAppFixture.NavigateAsync"/>
/// reaching the page marker) is the sole oracle for this test. Capture tries
/// Windows.Graphics.Capture app-window capture first (works without
/// foreground focus), then PrintWindow, then a safety-gated screen copy; a
/// failure of every method is recorded as an unavailable witness and must
/// never flip the oracle outcome. See
/// .agents/skills/windows-computer-use-proof/SKILL.md.
/// </summary>
[Collection(AccessibilityCollection.Name)]
public sealed class WindowsDesktopProofTests
{
    private const string ProofPageTag = "connection";
    private const string ProofPageMarker = "ConnectionPageMarker";

    private readonly AccessibilityAppFixture _app;
    private readonly ITestOutputHelper _output;

    public WindowsDesktopProofTests(AccessibilityAppFixture app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Accessibility")]
    public async Task ConnectionPage_IsReachableAndScreenshotable()
    {
        // Interactive-desktop guard: fail closed immediately in Session 0 or
        // any other non-interactive window station context, before attempting
        // navigation or capture. Without a desktop, the whole proof attempt
        // is meaningless, not just the screenshot half.
        if (TryGetNonInteractiveSessionReason(out var blockedReason))
        {
            throw new InvalidOperationException(
                "Windows desktop proof requires an interactive desktop session; refusing to " +
                $"start in a non-interactive context ({blockedReason}). Run this test on a " +
                "session with an interactive desktop (a real console/RDP session, or a hosted " +
                "CI runner such as windows-latest), not a Session 0 service context.");
        }

        var proof = new List<string>
        {
            $"head={Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_HEAD") ?? "local"}",
            $"route=hub/{ProofPageTag} marker={ProofPageMarker}",
        };

        // Oracle: the deterministic UI automation assertion. NavigateAsync
        // throws if the app does not reach the page marker in time, which is
        // the only signal that determines this test's pass/fail outcome.
        await _app.NavigateAsync(ProofPageTag, ProofPageMarker);
        proof.Add("navigation=ok");

        // Witness: best-effort screenshot capture. A failure here (for
        // example every capture method being unavailable or blank) is
        // recorded as an unavailable witness and must not fail the test;
        // the capture script still fails closed on the missing artifact, but
        // reports it as a distinct "artifact-missing" phase rather than an
        // "oracle-failed" app regression.
        //
        // Diagnostics privacy: CaptureHubScreenshotIfRequested collects
        // method-specific exception type/HResult/message for every capture
        // attempt (WindowsGraphicsCapture, PrintWindow, CopyFromScreen) in
        // _app.LastScreenshotDiagnostics. That detail is written only to this
        // test's private output (visible in the TRX / local test run), never
        // appended to `proof`, which becomes the redacted, publishable
        // proof.txt artifact. proof.txt only ever records a safe method name
        // or a generic exception type name on failure.
        string screenshotStatus;
        try
        {
            screenshotStatus = _app.CaptureHubScreenshotIfRequested() is { } screenshotPath
                ? $"screenshot=captured file={Path.GetFileName(screenshotPath)} bytes={new FileInfo(screenshotPath).Length} method={_app.LastScreenshotCaptureMethod ?? "unknown"}"
                : "screenshot=not_requested";
        }
        catch (Exception ex)
        {
            screenshotStatus = $"screenshot=unavailable reason={ex.GetType().Name}";
        }
        finally
        {
            if (_app.LastScreenshotDiagnostics.Count > 0)
            {
                _output.WriteLine("screenshot capture diagnostics (private test output only, not written to proof.txt):");
                foreach (var line in _app.LastScreenshotDiagnostics)
                    _output.WriteLine($"  {line}");
            }
        }
        proof.Add(screenshotStatus);

        proof.Add("result=pass");
        foreach (var line in proof)
            _output.WriteLine(line);

        WriteProofArtifactIfRequested(proof);
    }

    /// <summary>
    /// Detects Session 0 (service) or otherwise non-interactive window
    /// station contexts where a desktop UI, and therefore any screenshot
    /// witness, cannot exist. Hosted CI runners (for example GitHub Actions
    /// windows-latest) and normal developer/agent console or RDP sessions run
    /// with an interactive desktop and never trip this guard.
    /// </summary>
    private static bool TryGetNonInteractiveSessionReason(out string? reason)
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        if (sessionId == 0)
        {
            reason = $"process is running in Session 0 (service/non-interactive context), sessionId={sessionId}";
            return true;
        }
        if (!Environment.UserInteractive)
        {
            reason = "Environment.UserInteractive is false (non-interactive window station)";
            return true;
        }
        reason = null;
        return false;
    }

    private static void WriteProofArtifactIfRequested(IReadOnlyCollection<string> proof)
    {
        var configuredPath = Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_PATH");
        if (string.IsNullOrWhiteSpace(configuredPath))
            return;

        var path = Path.GetFullPath(configuredPath, Environment.CurrentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, proof);
    }
}
