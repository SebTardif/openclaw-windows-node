using System.Diagnostics;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

public sealed class WindowsSmokeNativeProcessTests
{
    private static readonly string Root = TestRepositoryPaths.GetRepositoryRoot();
    private static readonly string PowerShell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    [Fact]
    public void Helper_PreservesTypedArgumentsAndBenignStderr()
    {
        using var fixture = new NativeFixture(
            """
            param([string]$First, [string]$Second)
            [Console]::Out.Write($First + "|" + $Second)
            [Console]::Error.Write("benign stderr")
            exit 0
            """);

        var result = fixture.Invoke(
            timeoutSeconds: 10,
            "'-NoProfile', '-NonInteractive', '-File', $fixturePath, 'value with spaces', 'quote\"and\\slash'");

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("value with spaces|quote\"and\\slash", File.ReadAllText(result.StdoutPath));
        Assert.Equal("benign stderr", File.ReadAllText(result.StderrPath));
    }

    [Fact]
    public void Helper_PreservesNonzeroExitAndSeparateDiagnostics()
    {
        using var fixture = new NativeFixture(
            """
            [Console]::Out.Write("partial output")
            [Console]::Error.Write("expected failure")
            exit 23
            """);

        var result = fixture.Invoke(
            timeoutSeconds: 10,
            "'-NoProfile', '-NonInteractive', '-File', $fixturePath");

        Assert.False(result.TimedOut);
        Assert.Equal(23, result.ExitCode);
        Assert.Equal("partial output", File.ReadAllText(result.StdoutPath));
        Assert.Equal("expected failure", File.ReadAllText(result.StderrPath));
    }

    [Fact]
    public void Helper_WaitsForInheritedCaptureHandlesWithinOperationDeadline()
    {
        using var fixture = new NativeFixture(
            """
            param([string]$PowerShellPath)
            $startInfo = [Diagnostics.ProcessStartInfo]::new()
            $startInfo.FileName = $PowerShellPath
            $startInfo.Arguments = '-NoProfile -NonInteractive -Command "[Console]::Out.Write(''child output''); Start-Sleep -Seconds 2"'
            $startInfo.UseShellExecute = $false
            [void][Diagnostics.Process]::Start($startInfo)
            [Console]::Out.Write("parent output|")
            exit 0
            """);

        var result = fixture.Invoke(
            timeoutSeconds: 10,
            $"'-NoProfile', '-NonInteractive', '-File', $fixturePath, '{Escape(PowerShell)}'");

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ElapsedMilliseconds >= 1_500);
        Assert.Equal("parent output|child output", File.ReadAllText(result.StdoutPath));
        Assert.Equal("", File.ReadAllText(result.StderrPath));
    }

    [Fact]
    public void Helper_TimesOutAndTerminatesCapturedProcessTree()
    {
        using var fixture = new NativeFixture(
            """
            param([string]$PidPath, [string]$PowerShellPath)
            $child = [Diagnostics.Process]::Start(
                $PowerShellPath,
                '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 30"')
            [IO.File]::WriteAllText($PidPath, "$PID,$($child.Id)")
            Start-Sleep -Seconds 30
            """);
        var pidPath = Path.Combine(fixture.Root, "pids.txt");

        var result = fixture.Invoke(
            timeoutSeconds: 1,
            $"""
            '-NoProfile', '-NonInteractive', '-File', $fixturePath, '{Escape(pidPath)}', '{Escape(PowerShell)}'
            """);

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.True(result.ElapsedMilliseconds < 15_000);
        var pids = File.ReadAllText(pidPath).Split(',').Select(int.Parse).ToArray();
        Assert.Equal(2, pids.Length);
        Assert.All(pids, pid => Assert.False(IsRunning(pid), $"Process {pid} survived the bounded timeout."));
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed class NativeFixture : IDisposable
    {
        public NativeFixture(string fixtureSource)
        {
            Root = Path.Combine(Path.GetTempPath(), $"openclaw-native-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            FixturePath = Path.Combine(Root, "fixture.ps1");
            File.WriteAllText(FixturePath, fixtureSource);
        }

        public string Root { get; }
        private string FixturePath { get; }

        public NativeResult Invoke(int timeoutSeconds, string argumentExpression)
        {
            var harnessPath = Path.Combine(Root, $"harness-{Guid.NewGuid():N}.ps1");
            var resultPath = Path.Combine(Root, $"result-{Guid.NewGuid():N}.json");
            var helperPath = Path.Combine(
                WindowsSmokeNativeProcessTests.Root,
                "scripts",
                "_smoke-native-process.ps1");
            File.WriteAllText(
                harnessPath,
                $$"""
                $ErrorActionPreference = 'Stop'
                . '{{Escape(helperPath)}}'
                $fixturePath = '{{Escape(FixturePath)}}'
                $result = Invoke-SmokeNativeProcess `
                    -Operation 'native fixture' `
                    -FilePath '{{Escape(PowerShell)}}' `
                    -ArgumentList @({{argumentExpression}}) `
                    -TimeoutSeconds {{timeoutSeconds}} `
                    -CaptureRoot '{{Escape(Root)}}' `
                    -CaptureName 'native-fixture' `
                    -WorkingDirectory '{{Escape(Root)}}'
                $result | ConvertTo-Json -Compress | Set-Content -LiteralPath '{{Escape(resultPath)}}' -Encoding UTF8
                """);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = PowerShell,
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{harnessPath}\""
            }) ?? throw new InvalidOperationException("Failed to launch Windows PowerShell.");
            Assert.True(process.WaitForExit(30_000), "Windows PowerShell native helper harness timed out.");
            Assert.Equal(0, process.ExitCode);

            using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
            var root = document.RootElement;
            return new NativeResult(
                root.GetProperty("exitCode").ValueKind == JsonValueKind.Null
                    ? null
                    : root.GetProperty("exitCode").GetInt32(),
                root.GetProperty("timedOut").GetBoolean(),
                root.GetProperty("elapsedMilliseconds").GetInt64(),
                root.GetProperty("stdoutPath").GetString()!,
                root.GetProperty("stderrPath").GetString()!);
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed record NativeResult(
        int? ExitCode,
        bool TimedOut,
        long ElapsedMilliseconds,
        string StdoutPath,
        string StderrPath);
}
