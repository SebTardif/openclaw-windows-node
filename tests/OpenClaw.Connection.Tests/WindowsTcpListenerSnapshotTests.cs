using System.Diagnostics;

namespace OpenClaw.Connection.Tests;

public sealed class WindowsTcpListenerSnapshotTests
{
    [Fact]
    public void GetProcessCommandLine_InvalidPid_ReturnsNull()
    {
        Assert.Null(WindowsTcpListenerSnapshot.GetProcessCommandLine(0));
        Assert.Null(WindowsTcpListenerSnapshot.GetProcessCommandLine(-1));
    }

    [Fact]
    public async Task AwaitRedirectedOutput_ReturnsNullWhenStdoutNeverCloses()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);

        var never = new TaskCompletionSource<string>().Task;
        var helper = Task.Run(() =>
            WindowsTcpListenerSnapshot.AwaitRedirectedOutput(process, never, timeoutMs: 400));

        var completed = await Task.WhenAny(helper, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(helper, completed);
        Assert.Null(await helper);
    }
}
