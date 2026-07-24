using System.Diagnostics;
using System.Text.Json;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Executable, lightweight coverage for
/// scripts\capture-windows-desktop-proof.ps1's argument handling, isolation
/// guard, and manifest shape. These runs use -DryRun so they never build or
/// launch the tray app; they only exercise the script's own fail-closed
/// argument/path logic, which is fast enough to run as a normal test.
/// </summary>
public sealed class CaptureWindowsDesktopProofRunnerTests
{
    private static readonly string Root = TestRepositoryPaths.GetRepositoryRoot();
    private static readonly string ScriptPath = Path.Combine(Root, "scripts", "capture-windows-desktop-proof.ps1");

    [Fact]
    public void DryRun_ExitsNonZero_AndWritesNotRunManifest()
    {
        using var artifactRoot = new TempDirectory("desktop-proof-dryrun-");

        var (exitCode, _, stdErr) = RunScript($"-ArtifactRoot \"{artifactRoot.Path}\" -DryRun");

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, stdErr.Trim());

        var manifest = ReadManifest(artifactRoot.Path);
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("dry-run", manifest.RootElement.GetProperty("mode").GetString());
        Assert.Equal("not_run", manifest.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(2, manifest.RootElement.GetProperty("exitCode").GetInt32());
        Assert.True(manifest.RootElement.TryGetProperty("failure", out var failure));
        Assert.Equal(JsonValueKind.Null, failure.ValueKind);
        Assert.Equal("openclaw-windows-node", manifest.RootElement.GetProperty("repo").GetProperty("name").GetString());
        Assert.False(manifest.RootElement.GetProperty("repo").TryGetProperty("root", out _));

        var artifacts = manifest.RootElement.GetProperty("artifacts").EnumerateArray().ToList();
        var types = artifacts.Select(a => a.GetProperty("type").GetString()).ToList();
        Assert.Contains("screenshot", types);
        Assert.Contains("proof-text", types);
        Assert.Contains("trx", types);
        Assert.Contains("motion", types);
        Assert.All(
            artifacts.Where(a => a.GetProperty("path").ValueKind == JsonValueKind.String),
            artifact => Assert.Equal(Path.GetFileName(artifact.GetProperty("path").GetString()), artifact.GetProperty("path").GetString()));

        var motion = artifacts.Single(a => a.GetProperty("type").GetString() == "motion");
        Assert.Equal("unavailable", motion.GetProperty("status").GetString());
    }

    [Fact]
    public void DryRun_ManifestIncludesInteractiveDesktopEnvironmentInfo()
    {
        using var artifactRoot = new TempDirectory("desktop-proof-env-");

        var (exitCode, _, _) = RunScript($"-ArtifactRoot \"{artifactRoot.Path}\" -DryRun");

        Assert.Equal(2, exitCode);

        var manifest = ReadManifest(artifactRoot.Path);
        Assert.True(manifest.RootElement.TryGetProperty("environment", out var environment));
        Assert.Equal(JsonValueKind.Object, environment.ValueKind);
        Assert.True(environment.TryGetProperty("available", out _), "environment.available must be reported even for -DryRun.");
        Assert.True(environment.TryGetProperty("sessionId", out _), "environment.sessionId must be reported even for -DryRun.");
        Assert.True(environment.TryGetProperty("userInteractive", out _), "environment.userInteractive must be reported even for -DryRun.");
    }

    [Fact]
    public void IncludeMotion_FailsClosed_WithoutAttemptingCapture()
    {
        using var artifactRoot = new TempDirectory("desktop-proof-motion-");

        var (exitCode, _, _) = RunScript($"-ArtifactRoot \"{artifactRoot.Path}\" -DryRun -IncludeMotion");

        Assert.Equal(1, exitCode);

        var manifest = ReadManifest(artifactRoot.Path);
        Assert.Equal("fail", manifest.RootElement.GetProperty("outcome").GetString());
        Assert.True(manifest.RootElement.TryGetProperty("failure", out var failure));
        Assert.Equal(JsonValueKind.Object, failure.ValueKind);
        Assert.Equal("unsupported", failure.GetProperty("phase").GetString());
        Assert.Contains("no native screen/video recording primitive", failure.GetProperty("message").GetString());
    }

    [Fact]
    public void ArtifactRoot_UnderRealAppData_ThrowsAndWritesNoManifest()
    {
        using var artifactRoot = new TempDirectory("desktop-proof-appdata-guard-");
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        Assert.False(string.IsNullOrWhiteSpace(appData));
        var guardedPath = Path.Combine(appData!, "OpenClawTray", "openclaw-desktop-proof-test-guard");

        var (exitCode, _, stdErr) = RunScript($"-ArtifactRoot \"{guardedPath}\" -DryRun");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Refusing to write proof artifacts under real app data", stdErr);
        Assert.False(Directory.Exists(guardedPath), "The script must not create any directory under real %APPDATA%.");
    }

    [Fact]
    public void MissingRepoRoot_FailsClosedBeforeTouchingAnything()
    {
        using var artifactRoot = new TempDirectory("desktop-proof-badrepo-");
        var missingRepoRoot = Path.Combine(artifactRoot.Path, "does-not-exist");

        var (exitCode, _, stdErr) = RunScript($"-RepoRoot \"{missingRepoRoot}\" -DryRun");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Repository root does not exist", stdErr);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunScript(string arguments)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{ScriptPath}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start pwsh for the desktop proof script.");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        var exited = process.WaitForExit(TimeSpan.FromMinutes(2));
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("capture-windows-desktop-proof.ps1 -DryRun did not exit within 2 minutes.");
        }

        return (process.ExitCode, stdOut, stdErr);
    }

    private static JsonDocument ReadManifest(string artifactRoot)
    {
        var manifestPath = Directory.GetFiles(artifactRoot, "manifest.json", SearchOption.AllDirectories)
            .SingleOrDefault()
            ?? throw new InvalidOperationException($"No manifest.json was written under {artifactRoot}.");
        return JsonDocument.Parse(File.ReadAllText(manifestPath));
    }
}
