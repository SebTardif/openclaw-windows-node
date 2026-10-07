using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public class StaleSendReportTests
{
    [Fact]
    public void SupersededSend_IsNotAccepted()
    {
        StaleSendReport.MarkSuperseded();

        Assert.True(StaleSendReport.ConsumeSuperseded());
        Assert.False(StaleSendReport.ConsumeSuperseded());
    }

    [Fact]
    public void Provider_MarksAStaleDirectSend_AndThePortDoesNotAcceptIt()
    {
        var provider = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs"));
        var port = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ChatComposerRuntimePort.cs"));
        var stale = provider.IndexOf("if (!failure.IsCurrent)", StringComparison.Ordinal);
        Assert.True(stale >= 0);
        var window = provider.Substring(stale, Math.Min(700, provider.Length - stale));
        Assert.Contains("StaleSendReport.MarkSuperseded()", window, StringComparison.Ordinal);
        Assert.DoesNotContain("throw;", window, StringComparison.Ordinal);
        Assert.Contains("return !StaleSendReport.ConsumeSuperseded()", port, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
