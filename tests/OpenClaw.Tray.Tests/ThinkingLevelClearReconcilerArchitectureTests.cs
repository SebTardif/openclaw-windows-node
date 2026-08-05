namespace OpenClaw.Tray.Tests;

public sealed class ThinkingLevelClearReconcilerArchitectureTests
{
    [Fact]
    public void Provider_DoesNotOwnThinkingLevelClearReconciliationState()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var provider = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawChatDataProvider.cs"));
        var reconciler = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Chat",
            "ThinkingLevelClearReconciler.cs"));

        Assert.DoesNotContain("PendingThinkingLevelClear", provider);
        Assert.DoesNotContain("ThinkingLevelReconciliation", provider);
        Assert.DoesNotContain("_thinkingLevelClearVersions", provider);
        Assert.DoesNotContain("MaxThinkingLevelRefreshAttempts", provider);
        Assert.DoesNotContain("ScheduleThinkingLevelRetryAsync", provider);

        Assert.Contains("public sealed class ThinkingLevelClearReconciler", reconciler);
        Assert.Contains("public SnapshotResolution ApplyCorrelatedSnapshot", reconciler);
        Assert.Contains("public async Task<RefreshRequest?> RetryAfterFailureAsync", reconciler);
        Assert.Contains("public IReadOnlyList<RefreshRequest> OnConnectionChanged", reconciler);
    }
}
