namespace OpenClaw.Tray.Tests;

public sealed class SessionMaintenanceUiContractTests
{
    [Fact]
    public void ConfigPage_ExposesSessionMaintenanceEditor()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "ConfigPage.xaml");

        Assert.Contains("ConfigPageSessionMaintenance", xaml, StringComparison.Ordinal);
        Assert.Contains("MaintenanceModeComboBox", xaml, StringComparison.Ordinal);
        Assert.Contains("PruneAfterTextBox", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxEntriesNumberBox", xaml, StringComparison.Ordinal);
        Assert.Contains("ResetArchiveRetentionTextBox", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxDiskBytesTextBox", xaml, StringComparison.Ordinal);
        Assert.Contains("HighWaterBytesTextBox", xaml, StringComparison.Ordinal);
        Assert.Contains("Changes use the Save changes button below", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigPage_StagesMaintenanceInExistingOptimisticConfigPatch()
    {
        var source = Read("src", "OpenClaw.Tray.WinUI", "Pages", "ConfigPage.xaml.cs");

        Assert.Contains("ConfigEditorModel.CaptureSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("ApplyMaintenanceDraft", source, StringComparison.Ordinal);
        Assert.Contains("_pendingChanges[$\"{section}.maxEntries\"]", source, StringComparison.Ordinal);
        Assert.Contains("PatchConfigDetailedAsync(updated, saveBase.BaseHash)", source, StringComparison.Ordinal);
        Assert.Contains("LooksLikeStaleBaseHash", source, StringComparison.Ordinal);
        Assert.Contains("OperatorScopeHelper.CanWriteConfig", source, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               (!File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")) ||
                !Directory.Exists(Path.Combine(directory.FullName, "src"))))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
