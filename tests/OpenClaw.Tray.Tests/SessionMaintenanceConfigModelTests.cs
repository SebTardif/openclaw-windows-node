using System.Text.Json;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class SessionMaintenanceConfigModelTests
{
    [Fact]
    public void Read_UsesGatewayValuesAndDefaultsForOmittedFields()
    {
        using var document = JsonDocument.Parse("""
        {
          "baseHash": "abc",
          "parsed": {
            "session": {
              "maintenance": {
                "mode": "warn",
                "maxEntries": 125,
                "resetArchiveRetention": "14d",
                "maxDiskBytes": false
              }
            }
          }
        }
        """);

        var settings = SessionMaintenanceConfigModel.Read(document.RootElement);

        Assert.Equal("warn", settings.Mode);
        Assert.Equal("30d", settings.PruneAfter);
        Assert.Equal(125, settings.MaxEntries);
        Assert.False(settings.KeepResetArchives);
        Assert.Equal("14d", settings.ResetArchiveRetention);
        Assert.False(settings.LimitDiskUsage);
        Assert.Equal("", settings.HighWaterBytes);
    }

    [Fact]
    public void Validate_RejectsInvalidDurationsCountsAndSizes()
    {
        var settings = new SessionMaintenanceSettings(
            "other", "never", 0, false, "0d", true, "lots", "-1gb");

        var errors = SessionMaintenanceConfigModel.Validate(settings);

        Assert.Equal(6, errors.Count);
        Assert.Contains("Mode", errors.Keys);
        Assert.Contains("Prune after", errors.Keys);
        Assert.Contains("Maximum entries", errors.Keys);
        Assert.Contains("Reset archive retention", errors.Keys);
        Assert.Contains("Maximum disk usage", errors.Keys);
        Assert.Contains("High-water target", errors.Keys);
    }

    [Fact]
    public void Validate_MatchesGatewayDurationAndByteGrammar()
    {
        var valid = new SessionMaintenanceSettings(
            "enforce", "1h30m", 500, false, "2m500ms", true, "10g", "512mb");
        var invalidSpacing = valid with { PruneAfter = "30 d", MaxDiskBytes = "10 gb" };

        Assert.Empty(SessionMaintenanceConfigModel.Validate(valid));
        var errors = SessionMaintenanceConfigModel.Validate(invalidSpacing);
        Assert.Contains("Prune after", errors.Keys);
        Assert.Contains("Maximum disk usage", errors.Keys);
    }
}
