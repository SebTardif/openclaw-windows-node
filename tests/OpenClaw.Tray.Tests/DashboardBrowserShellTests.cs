using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class DashboardBrowserShellTests
{
    [Fact]
    public void TryOpen_InvalidTarget_ReturnsFalseWithoutAProcessId()
    {
        Assert.False(DashboardBrowserShell.TryOpen("", out var processId));
        Assert.Null(processId);
    }

    [Fact]
    public void InterpretStartedProcess_NullProcess_IsSuccessfulActivationWithoutAProcessId()
    {
        Assert.True(DashboardBrowserShell.InterpretStartedProcess(null, out var processId));
        Assert.Null(processId);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" -- \"%1\"", "chrome")]
    [InlineData("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -osint -url \"%1\"", "firefox")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ProcessNameFromOpenCommand_ReadsTheExecutableName(string? command, string? expected)
    {
        Assert.Equal(expected, DashboardBrowserShell.ProcessNameFromOpenCommand(command));
    }
}
