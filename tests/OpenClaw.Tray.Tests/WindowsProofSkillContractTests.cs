namespace OpenClaw.Tray.Tests;

public sealed class WindowsProofSkillContractTests
{
    private static readonly string Root = TestRepositoryPaths.GetRepositoryRoot();

    [Fact]
    public void WindowsNodeTestingSkill_CoversEveryRequiredLaneWithoutMacTooling()
    {
        var skill = Read(".agents", "skills", "windows-node-testing", "SKILL.md");

        foreach (var lane in new[]
                 {
                     "Unit lane",
                     "UI lane",
                     "Accessibility lane",
                     "Local MCP lane",
                     "Gateway lane",
                     "Installed Inno lane",
                     "Live lane",
                     "Performance lane",
                     "Clean-runner lane",
                 })
        {
            Assert.Contains(lane, skill);
        }

        Assert.Contains(@".\build.ps1", skill);
        Assert.Contains("winnode --list-tools", skill);
        Assert.Contains(@".\scripts\validate-installed-inno-smoke.ps1", skill);
        Assert.Contains("scripts/ci-changed-scope.mjs", skill);
        Assert.DoesNotContain("Peekaboo", skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Parallels", skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Swift", skill, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputerUseProofSkill_DefinesOracleCurationAndFailClosedContracts()
    {
        var skill = Read(".agents", "skills", "windows-computer-use-proof", "SKILL.md");

        Assert.Contains("Witness, not oracle", skill);
        Assert.Contains("environment-non-interactive", skill);
        Assert.Contains("artifact-missing", skill);
        Assert.Contains("schemaVersion: 1", skill);
        Assert.Contains("profile/auth directories", skill);
        Assert.Contains("raw process logs", skill);
        Assert.Contains("publish", skill);
        Assert.Contains("## Real behavior proof", skill);
        Assert.DoesNotContain("Peekaboo", skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Parallels", skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Swift", skill, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProofArtifacts_OmitAbsoluteHostPathsFromCuratedFiles()
    {
        var script = Read("scripts", "capture-windows-desktop-proof.ps1");
        var proofTest = Read("tests", "OpenClaw.Tray.UITests", "WindowsDesktopProofTests.cs");

        Assert.Contains("name             = $RepoName", script);
        Assert.Contains("workingTreeDirty = $WorkingTreeDirty", script);
        Assert.DoesNotContain("root   = $RepoRootPath", script);
        Assert.Contains("path = [IO.Path]::GetFileName($ScreenshotPath)", script);
        Assert.Contains("publish = $false", script);
        Assert.DoesNotContain("dataDir=", proofTest);
        Assert.DoesNotContain("ex.Message", proofTest);
    }

    [Fact]
    public void ScreenshotCapture_TriesWindowsGraphicsCaptureBeforePrintWindowBeforeScreenCopyFallback()
    {
        var fixture = Read("tests", "OpenClaw.Tray.UITests", "AccessibilityAppFixture.cs");

        var attemptsIndex = fixture.IndexOf("var attempts = new[]", StringComparison.Ordinal);
        var wgcIndex = fixture.IndexOf("WindowsGraphicsCaptureMethod, () => TryWindowsGraphicsCapture", StringComparison.Ordinal);
        var printWindowAttemptIndex = fixture.IndexOf("PrintWindowCaptureMethod, () => TryPrintWindow", StringComparison.Ordinal);
        var screenCopyAttemptIndex = fixture.IndexOf("CopyFromScreenCaptureMethod, () => TrySafeForegroundScreenCopy", StringComparison.Ordinal);
        Assert.True(attemptsIndex >= 0, "Expected a single ordered capture-attempts list.");
        Assert.True(wgcIndex > attemptsIndex, "Expected the WindowsGraphicsCapture attempt in the capture-attempts list.");
        Assert.True(printWindowAttemptIndex > wgcIndex, "WindowsGraphicsCapture must be attempted before PrintWindow.");
        Assert.True(screenCopyAttemptIndex > printWindowAttemptIndex, "PrintWindow must be attempted before the screen-copy fallback.");

        var printWindowIndex = fixture.IndexOf("PrintWindow(HubWindowHandle", StringComparison.Ordinal);
        var copyFromScreenIndex = fixture.IndexOf("graphics.CopyFromScreen(", StringComparison.Ordinal);
        Assert.True(printWindowIndex >= 0, "Expected app-scoped PrintWindow capture.");
        Assert.True(copyFromScreenIndex >= 0, "Expected the existing screen-copy fallback.");
        Assert.True(printWindowIndex < copyFromScreenIndex, "PrintWindow must be attempted before screen-copy fallback.");
        Assert.Contains("HasMeaningfulVisualContent", fixture);
        Assert.Contains("AttachThreadInput(currentThreadId, foregroundThreadId, attach: true)", fixture);
        Assert.Contains("return GetForegroundWindow() == HubWindowHandle", fixture);
    }

    [Fact]
    public void ScreenshotCapture_GatesScreenCopyFallbackOnVisibilityForegroundAndUnobscuredZOrder()
    {
        var fixture = Read("tests", "OpenClaw.Tray.UITests", "AccessibilityAppFixture.cs");

        Assert.Contains("TryVerifyHubWindowSafeToScreenCopy", fixture);
        Assert.Contains("IsWindowVisible(HubWindowHandle)", fixture);
        Assert.Contains("IsIconic(HubWindowHandle)", fixture);
        Assert.Contains("IsWindowCloaked(HubWindowHandle)", fixture);
        Assert.Contains("is not fully on-screen", fixture);
        Assert.Contains("is not the foreground window", fixture);
        Assert.Contains("is obscured by another visible window", fixture);

        // The z-order walk must fail closed if it never actually reaches the
        // Hub handle: an "unobscured" result is only trustworthy if every
        // window enumerated before the Hub in z-order was checked.
        Assert.Contains("reachedHub", fixture);
        Assert.Contains("could not be proven", fixture);
    }

    [Fact]
    public void GraphicsCaptureInterop_UsesOfficialGraphicsCaptureItemIidForWindowCreationAndPreservesMonitorPath()
    {
        var interop = Read("src", "OpenClaw.Tray.WinUI", "Services", "GraphicsCaptureInterop.cs");

        // IID_IGraphicsCaptureItem, required by IGraphicsCaptureItemInterop.CreateForWindow.
        Assert.Contains("79C3F95B-31F7-4EC2-A464-632EF5D30760", interop);

        var windowFactoryIndex = interop.IndexOf("internal static GraphicsCaptureItem CreateCaptureItemForWindow", StringComparison.Ordinal);
        var monitorFactoryIndex = interop.IndexOf("internal static GraphicsCaptureItem CreateCaptureItemForMonitor", StringComparison.Ordinal);
        Assert.True(windowFactoryIndex >= 0, "Expected CreateCaptureItemForWindow.");
        Assert.True(monitorFactoryIndex >= 0, "Expected CreateCaptureItemForMonitor.");

        var windowFactoryBody = interop.Substring(windowFactoryIndex, windowFactoryIndex < monitorFactoryIndex
            ? monitorFactoryIndex - windowFactoryIndex
            : interop.Length - windowFactoryIndex);
        Assert.Contains("factory.CreateForWindow(hwnd, in IidGraphicsCaptureItem", windowFactoryBody);

        var monitorFactoryBody = interop.Substring(monitorFactoryIndex, monitorFactoryIndex < windowFactoryIndex
            ? windowFactoryIndex - monitorFactoryIndex
            : interop.Length - monitorFactoryIndex);
        // The monitor path already works in production (screen recording);
        // it must keep using IID_IInspectable rather than being switched to
        // the window-specific IID alongside this change.
        Assert.Contains("factory.CreateForMonitor(hMonitor, in IidInspectable", monitorFactoryBody);
    }

    [Fact]
    public void WindowsGraphicsCapture_NeverUsesPickerOrConsentOrCapabilityApis()
    {
        var fixture = Read("tests", "OpenClaw.Tray.UITests", "AccessibilityAppFixture.cs");
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Services", "WindowGraphicsCaptureHelper.cs");
        var interop = Read("src", "OpenClaw.Tray.WinUI", "Services", "GraphicsCaptureInterop.cs");

        // The picker/consent/capability APIs must never be *used* by any of
        // these files. WindowGraphicsCaptureHelper's own doc comment names
        // GraphicsCapturePicker/RequestAccessAsync in prose to say they are
        // deliberately not used, so check for real usage patterns rather
        // than a bare substring match that would also flag that comment.
        foreach (var source in new[] { fixture, helper, interop })
        {
            Assert.DoesNotContain("new GraphicsCapturePicker", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".PickSingleItemAsync(", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".RequestAccessAsync(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GraphicsCaptureAccess.", source, StringComparison.Ordinal);
        }

        Assert.Contains("CreateForWindow(hwnd", interop);
        Assert.Contains("IsSupported()", helper);
        // IsSupported() must be a direct passthrough with no swallowing catch,
        // so a capability-query failure surfaces as real exception evidence
        // to the caller rather than a silently-returned false.
        Assert.Contains("internal static bool IsSupported() => GraphicsCaptureSession.IsSupported();", helper);
    }

    [Fact]
    public void ScreenRecordingService_ReusesSharedGraphicsCaptureInteropInsteadOfADuplicateD3DDeviceStack()
    {
        var recordingService = Read("src", "OpenClaw.Tray.WinUI", "Services", "ScreenRecordingService.cs");
        var interop = Read("src", "OpenClaw.Tray.WinUI", "Services", "GraphicsCaptureInterop.cs");

        Assert.Contains("GraphicsCaptureInterop.CreateDirect3DDevice()", recordingService);
        Assert.Contains("GraphicsCaptureInterop.CreateCaptureItemForMonitor(", recordingService);
        Assert.DoesNotContain("D3D11CreateDevice", recordingService);
        Assert.Contains("D3D11CreateDevice", interop);
    }

    [Fact]
    public void Workflow_IsManualOnlyAndPreservesFailureArtifacts()
    {
        var workflow = Read(".github", "workflows", "windows-desktop-proof.yml");

        Assert.Contains("workflow_dispatch:", workflow);
        Assert.DoesNotContain("\n  push:", workflow);
        Assert.DoesNotContain("\n  pull_request:", workflow);
        Assert.DoesNotContain("\n  issue_comment:", workflow);
        Assert.Contains("if: always()", workflow);
        Assert.Contains("!TestResults/DesktopProof/**/*.trx", workflow);
        Assert.Contains("if-no-files-found: error", workflow);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root }.Concat(parts).ToArray()));
}
