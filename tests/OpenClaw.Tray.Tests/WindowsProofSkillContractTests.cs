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
    public void ScreenshotCapture_PrefersAppScopedPrintWindowBeforeScreenCopyFallback()
    {
        var fixture = Read("tests", "OpenClaw.Tray.UITests", "AccessibilityAppFixture.cs");

        var printWindowIndex = fixture.IndexOf("PrintWindow(HubWindowHandle", StringComparison.Ordinal);
        var copyFromScreenIndex = fixture.IndexOf("graphics.CopyFromScreen(", StringComparison.Ordinal);
        Assert.True(printWindowIndex >= 0, "Expected app-scoped PrintWindow capture.");
        Assert.True(copyFromScreenIndex >= 0, "Expected the existing screen-copy fallback.");
        Assert.True(printWindowIndex < copyFromScreenIndex, "PrintWindow must be attempted before screen-copy fallback.");
        Assert.Contains("LastScreenshotCaptureMethod = \"PrintWindow\"", fixture);
        Assert.Contains("AttachThreadInput(currentThreadId, foregroundThreadId, attach: true)", fixture);
        Assert.Contains("return GetForegroundWindow() == HubWindowHandle", fixture);
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
