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
    public void WindowsNodeTestingSkill_CoversInstalledAndCleanMachineUpgradeContracts()
    {
        var skill = Read(".agents", "skills", "windows-node-testing", "SKILL.md");

        Assert.Contains(@".\scripts\validate-installed-inno-smoke.ps1", skill);
        Assert.Contains(@".\scripts\validate-inno-upgrade-smoke.ps1", skill);
        Assert.Contains("-PreviousRelease v0.6.12", skill);
        Assert.Contains("-PreviousInstallerSha256 <official-x64-installer-sha256>", skill);
        Assert.Contains("-ConfirmCleanMachineReleaseIdentity", skill);
        Assert.Contains("disposable Windows VM or clean runner", skill);
        Assert.Contains("docs/WINDOWS_NODE_TESTING.md", skill);
    }

    [Fact]
    public void WindowsNodeTestingSkill_RoutesLiveParityWithoutNormalCiCredentials()
    {
        var skill = Read(".agents", "skills", "windows-node-testing", "SKILL.md");

        Assert.Contains(@"tests\OpenClaw.E2ETests\LiveParity", skill);
        Assert.Contains(@".\scripts\validate-live-parity-e2e.ps1 -Lane LiveModel", skill);
        Assert.Contains(@".\scripts\validate-live-parity-e2e.ps1 -Lane RealChannel", skill);
        Assert.Contains("docs/LIVE_PARITY_TESTING.md", skill);
        Assert.Contains("secretless gate, profile, and", skill);
        Assert.Contains("redaction contract tests", skill);
        Assert.Contains("must never", skill);
        Assert.Contains("run in normal hosted CI", skill);
        Assert.Contains("LiveModelE2ETests", skill);
        Assert.Contains("RealChannelE2ETests", skill);
        Assert.DoesNotContain("There is no separate\n\"live\" test project", skill);
        Assert.DoesNotContain("There is no separate \"live\" test project", skill);
    }

    [Fact]
    public void WindowsNodeTestingSkill_RoutesCleanRunnerControllersAndProofTaxonomy()
    {
        var skill = Read(".agents", "skills", "windows-node-testing", "SKILL.md");

        Assert.Contains("docs/CLEAN_WINDOWS_RUNNERS.md", skill);
        Assert.Contains(@".\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1", skill);
        Assert.Contains(@".\scripts\clean-windows\Install-Crabbox.ps1", skill);
        Assert.Contains(@".\scripts\clean-windows\Invoke-CrabboxWindowsSmoke.ps1", skill);
        Assert.Contains("NativeDesktopComponent", skill);
        Assert.Contains("Wsl2Component", skill);
        Assert.Contains("CombinedInstalledSmoke", skill);
        Assert.Contains("require elevation", skill);
        Assert.Contains("Azure RBAC", skill);
        Assert.Contains("exact", skill);
        Assert.Contains("lease-id capture and cleanup", skill);
    }

    [Fact]
    public void WindowsNodeTestingSkill_KeepsDesktopAndProofValidationContractsLinked()
    {
        var skill = Read(".agents", "skills", "windows-node-testing", "SKILL.md");

        Assert.Contains(".agents/skills/windows-computer-use-proof/SKILL.md", skill);
        Assert.Contains(".agents/skills/openclaw-proof-validation/SKILL.md", skill);
        Assert.Contains("[self-hosted, windows, openclaw-desktop-proof]", skill);
        Assert.Contains("active,", skill);
        Assert.Contains("interactive desktop", skill);
        Assert.Contains("fails closed", skill);
    }

    [Fact]
    public void WindowsNodeTestingSkill_RoutesLocalCleanMachineProofToHyperVSkill()
    {
        var skill = Read(".agents", "skills", "windows-node-testing", "SKILL.md");

        Assert.Contains(".agents/skills/openclaw-hyperv-smoke/SKILL.md", skill);
        Assert.Contains("local Hyper-V clean-machine", skill);
        Assert.Contains("fixed-checkpoint", skill);
        Assert.Contains("restore-in-finally", skill);
    }

    [Fact]
    public void HyperVSmokeSkill_DefinesOwnedLifecycleAndWindowsNativeTransport()
    {
        var skill = Read(".agents", "skills", "openclaw-hyperv-smoke", "SKILL.md");

        Assert.Contains("name: openclaw-hyperv-smoke", skill);
        Assert.Contains("Get-VM", skill);
        Assert.Contains("Get-VMSnapshot", skill);
        Assert.Contains("Checkpoint-VM", skill);
        Assert.Contains("Restore-VMSnapshot", skill);
        Assert.Contains("Start-VM", skill);
        Assert.Contains("Stop-VM", skill);
        Assert.Contains("Invoke-Command -VMName", skill);
        Assert.Contains("Copy-Item -ToSession", skill);
        Assert.Contains("Copy-Item -FromSession", skill);
        Assert.Contains("clean-windows", skill);
        Assert.Contains("openclaw-prerequisites", skill);
        Assert.Contains("-ConfirmOwnedAction", skill);
        Assert.Contains("Generation 2 x64", skill);
        Assert.Contains("nested virtualization", skill);
        Assert.Contains("WSL2", skill);
        Assert.Contains("Ubuntu", skill);
        Assert.DoesNotContain("prlctl", skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Parallels", skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("macOS", skill, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HyperVSmokeSkill_GuardsCommandsPhasesOwnershipAndRelationships()
    {
        var skill = Read(".agents", "skills", "openclaw-hyperv-smoke", "SKILL.md");

        foreach (var command in new[] { "Create", "Prepare", "Verify", "Smoke", "Restore" })
        {
            Assert.Contains($"-Command {command}", skill);
        }

        Assert.Contains("-ValidationLane Installed", skill);
        Assert.Contains("-ValidationLane Upgrade", skill);
        Assert.Contains("-PreviousRelease v0.6.12", skill);
        Assert.Contains("-PreviousInstallerSha256", skill);
        Assert.Contains("-ConfirmCleanMachineReleaseIdentity", skill);
        Assert.Contains("Never report it as", skill);
        Assert.Contains("upgrade proof", skill);
        Assert.Contains("Never delete or unregister a VM, VHD, or checkpoint", skill);
        Assert.Contains("Never modify, restore,", skill);
        Assert.Contains("start, or stop an unowned", skill);
        Assert.Contains("restore", skill);
        Assert.Contains("`openclaw-prerequisites` in a `finally` path", skill);
        Assert.Contains("phase-status.json", skill);
        Assert.Contains("cleanupCompleted: true", skill);
        Assert.Contains("non-elevated", skill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing ISO", skill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing credential", skill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("active conflicting VM", skill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".agents/skills/windows-node-testing/SKILL.md", skill);
        Assert.Contains(".agents/skills/openclaw-proof-validation/SKILL.md", skill);
        Assert.Contains(".agents/skills/windows-computer-use-proof/SKILL.md", skill);
        Assert.Contains(".agents/skills/crabbox/SKILL.md", skill);
    }

    [Fact]
    public void HyperVSkills_RouteUnattendedCreateCredentialAndDesktopSecurity()
    {
        var hyperV = Read(".agents", "skills", "openclaw-hyperv-smoke", "SKILL.md");
        var testing = Read(".agents", "skills", "windows-node-testing", "SKILL.md");
        var docs = Read("docs", "CLEAN_WINDOWS_RUNNERS.md");
        var combined = string.Join("\n", hyperV, testing, docs);

        Assert.Contains("Win11_Enterprise_Eval_25H2_en-us_x64.iso", combined);
        Assert.Contains(
            "A61ADEAB895EF5A4DB436E0A7011C92A2FF17BB0357F58B13BBC4062E535E7B9",
            combined);
        Assert.Contains("Windows 11 Enterprise Evaluation", combined);
        Assert.Contains("-GenerateCredential", combined);
        Assert.Contains("requires `-GenerateCredential` as explicit consent", combined);
        Assert.Contains("-CredentialPath", combined);
        Assert.Contains("Test-CleanWindowsUnattendMedia.ps1", combined);
        Assert.Contains("-ResumeUnattended", combined);
        Assert.Contains("-CleanupUnattend", combined);
        Assert.Contains("DPAPI", combined);
        Assert.Contains("IMAPI2", combined);
        Assert.Contains("before any VM or VHD creation", combined);
        Assert.Contains("classified authentication", combined);
        Assert.Contains(@"root\virtualization\v2", combined);
        Assert.Contains("Msvm_Keyboard.TypeKey", combined);
        Assert.Contains("trusted VM ID", combined);
        Assert.Contains("fixed 7-second", combined);
        Assert.Contains("multiple 750 ms", combined);
        Assert.Contains("does not stop", combined);
        Assert.Contains("Ordinary resume paths do not inject keys", combined);
        Assert.Contains("sole resume exception", combined);
        Assert.Contains("configuration was repaired", combined);
        Assert.Contains("reverified", combined);
        Assert.Contains("does not enable autologon", testing);
        Assert.Contains("explicit interactive sign-in", combined);
        Assert.Contains("never deletes", combined);
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

    [Fact]
    public void Workflow_RequiresFixedDesktopCapableSelfHostedRunner()
    {
        var workflow = Read(".github", "workflows", "windows-desktop-proof.yml");

        Assert.Contains("runs-on: [self-hosted, windows, openclaw-desktop-proof]", workflow);
        Assert.DoesNotContain("runs-on: windows-latest", workflow);
    }

    [Fact]
    public void Workflow_InstallsMatchingWindowsAppRuntimeBeforeDesktopProof()
    {
        var workflow = Read(".github", "workflows", "windows-desktop-proof.yml");

        Assert.Contains("MicrosoftWindowsAppSDKVersion", workflow);
        Assert.Contains("windowsappruntimeinstall-x64.exe", workflow);
        Assert.Contains("& $exe --quiet", workflow);
        Assert.True(
            workflow.IndexOf("- name: Install WindowsAppRuntime", StringComparison.Ordinal) <
            workflow.IndexOf("- name: Capture Windows desktop proof", StringComparison.Ordinal),
            "The matching Windows App Runtime must be installed before the proof launches WinUI.");
        Assert.DoesNotContain("${{ inputs.runner", workflow);
        Assert.Contains("$sessionId -eq 0", workflow);
        Assert.Contains("[Environment]::UserInteractive", workflow);
        Assert.Contains("qwinsta.exe", workflow);
        Assert.Contains(@"-notmatch '(?im)\bActive\b'", workflow);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root }.Concat(parts).ToArray()));
}
