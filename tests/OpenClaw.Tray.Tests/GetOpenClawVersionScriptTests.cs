using System.Diagnostics;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

public sealed class GetOpenClawVersionScriptTests
{
    [Fact]
    public void VersionScript_UsesBoundedExactDotNetCaptureUnderWindowsPowerShell()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openclaw-version-test-{Guid.NewGuid():N}");
        var fakeDirectory = Path.Combine(root, "fake");
        var captureDirectory = Path.Combine(root, "captures");
        Directory.CreateDirectory(fakeDirectory);
        Directory.CreateDirectory(captureDirectory);
        try
        {
            var fakeDotNet = BuildFakeDotNet(fakeDirectory);
            var scriptPath = Path.Combine(
                TestRepositoryPaths.GetRepositoryRoot(),
                "scripts",
                "Get-OpenClawVersion.ps1");

            var success = RunScenario(scriptPath, fakeDotNet, captureDirectory, "success", 10);
            AssertScenarioSucceeded(success);
            Assert.Equal("1.2.3-alpha.4", success.GetProperty("value").GetString());

            var restoreFailure = RunScenario(
                scriptPath,
                fakeDotNet,
                captureDirectory,
                "restore-fail",
                10);
            Assert.Contains(
                "dotnet.exe Restore failed with exit code 17",
                restoreFailure.GetProperty("error").GetString(),
                StringComparison.Ordinal);
            Assert.Contains(
                string.Concat("to", "ken=<redacted>"),
                restoreFailure.GetProperty("error").GetString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                string.Concat("fixture-", "sensitive-value"),
                restoreFailure.GetProperty("error").GetString(),
                StringComparison.Ordinal);

            var gitVersionFailure = RunScenario(
                scriptPath,
                fakeDotNet,
                captureDirectory,
                "gitversion-fail",
                10);
            Assert.Contains(
                "dotnet.exe GitVersion failed with exit code 23",
                gitVersionFailure.GetProperty("error").GetString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "GitVersion crashed",
                gitVersionFailure.GetProperty("error").GetString(),
                StringComparison.Ordinal);

            var malformedJson = RunScenario(
                scriptPath,
                fakeDotNet,
                captureDirectory,
                "malformed",
                10);
            Assert.Contains(
                "GitVersion stdout was not valid JSON",
                malformedJson.GetProperty("error").GetString(),
                StringComparison.Ordinal);

            var timeout = RunScenario(scriptPath, fakeDotNet, captureDirectory, "timeout", 1);
            Assert.Contains(
                "dotnet.exe Restore timed out after 1 seconds",
                timeout.GetProperty("error").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    private static JsonElement RunScenario(
        string scriptPath,
        string fakeDotNet,
        string captureDirectory,
        string scenario,
        int timeoutSeconds)
    {
        var fakeDirectory = Path.GetDirectoryName(fakeDotNet)
            ?? throw new InvalidOperationException("Fake dotnet directory was not resolved.");
        var command = string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$env:PATH = ", PsQuote(fakeDirectory), " + ';' + $env:PATH\n",
            "$env:OPENCLAW_FAKE_DOTNET_SCENARIO = ", PsQuote(scenario), "\n",
            "$env:DOTNET_NOLOGO = 'before-nologo'\n",
            "Remove-Item Env:DOTNET_CLI_TELEMETRY_OPTOUT -ErrorAction SilentlyContinue\n",
            "$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = 'before-skip'\n",
            "$errorMessage = $null\n",
            "$value = $null\n",
            "try {\n",
            " $value = @(& ", PsQuote(scriptPath),
            " -Variable SemVer -NativeTimeoutSec ",
            timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ") -join ''\n",
            "} catch { $errorMessage = $_.Exception.Message }\n",
            "$remaining = @(Get-ChildItem -LiteralPath ",
            PsQuote(captureDirectory),
            " -Directory -Filter 'openclaw-version-*' -ErrorAction SilentlyContinue).Count\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            " error = $errorMessage\n",
            " value = $value\n",
            " remainingCaptures = $remaining\n",
            " nologo = $env:DOTNET_NOLOGO\n",
            " telemetryExists = Test-Path Env:DOTNET_CLI_TELEMETRY_OPTOUT\n",
            " skipFirstTime = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE\n",
            "} | ConvertTo-Json -Compress))\n");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["TEMP"] = captureDirectory;
        startInfo.Environment["TMP"] = captureDirectory;
        foreach (var argument in new[] { "-NoProfile", "-Command", command })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Windows PowerShell.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Version script proof exceeded 30 seconds.");
        }
        Assert.True(
            process.ExitCode == 0,
            $"PowerShell proof failed with {process.ExitCode}.{Environment.NewLine}{stderr}");
        using var document = JsonDocument.Parse(stdout);
        var result = document.RootElement.Clone();
        Assert.Equal(0, result.GetProperty("remainingCaptures").GetInt32());
        Assert.Equal("before-nologo", result.GetProperty("nologo").GetString());
        Assert.False(result.GetProperty("telemetryExists").GetBoolean());
        Assert.Equal("before-skip", result.GetProperty("skipFirstTime").GetString());
        return result;
    }

    private static void AssertScenarioSucceeded(JsonElement result)
    {
        Assert.True(
            string.IsNullOrEmpty(result.GetProperty("error").GetString()),
            result.GetProperty("error").GetString());
    }

    private static string BuildFakeDotNet(string directory)
    {
        const string source =
            """
            using System;
            using System.Threading;

            internal static class Program
            {
                private static int Main(string[] args)
                {
                    string scenario = Environment.GetEnvironmentVariable("OPENCLAW_FAKE_DOTNET_SCENARIO") ?? "";
                    bool restore = args.Length == 2 && args[0] == "tool" && args[1] == "restore";
                    bool gitVersion = args.Length == 6 &&
                        args[0] == "tool" &&
                        args[1] == "run" &&
                        args[2] == "dotnet-gitversion" &&
                        args[3] == "--" &&
                        args[4] == "/output" &&
                        args[5] == "json";
                    if (!restore && !gitVersion)
                    {
                        Console.Error.Write("unexpected arguments: " + string.Join("|", args));
                        return 91;
                    }
                    if (scenario == "timeout" && restore)
                    {
                        Thread.Sleep(5000);
                        return 0;
                    }
                    if (scenario == "restore-fail" && restore)
                    {
                        Console.Out.Write("restore output");
                        Console.Error.Write(string.Concat("to", "ken=fixture-sensitive-value restore failed"));
                        return 17;
                    }
                    if (restore)
                    {
                        Console.Error.Write("benign restore warning");
                        return 0;
                    }
                    if (scenario == "gitversion-fail")
                    {
                        Console.Out.Write("partial output");
                        Console.Error.Write("GitVersion crashed");
                        return 23;
                    }
                    if (scenario == "malformed")
                    {
                        Console.Out.Write("{not-json}");
                        return 0;
                    }
                    Console.Out.Write("{\"SemVer\":\"1.2.3-alpha.4\",\"MajorMinorPatch\":\"1.2.3\"}");
                    Console.Error.Write("benign GitVersion warning");
                    return 0;
                }
            }
            """;
        var sourcePath = Path.Combine(directory, "FakeDotNet.cs");
        var outputPath = Path.Combine(directory, "dotnet.exe");
        File.WriteAllText(sourcePath, source);
        var frameworkRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var compiler = Path.Combine(
            frameworkRoot,
            "Microsoft.NET",
            Environment.Is64BitOperatingSystem ? "Framework64" : "Framework",
            "v4.0.30319",
            "csc.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = compiler,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/nologo", "/target:exe", $"/out:{outputPath}", sourcePath })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Windows C# compiler.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Fake dotnet compilation timed out.");
        Assert.True(
            process.ExitCode == 0 && File.Exists(outputPath),
            $"Fake dotnet compilation failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        return outputPath;
    }

    private static string PsQuote(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static void DeleteTestDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        Directory.Delete(path, recursive: true);
    }
}
