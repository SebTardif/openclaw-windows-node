using System.Text.Json;
using OpenClawTray.Helpers;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class ConfigEditorModelTests
{
    [Fact]
    public void CaptureSnapshot_UsesParsedRootAndBaseHash()
    {
        using var document = JsonDocument.Parse("""
        {
          "path": "/tmp/openclaw.json",
          "baseHash": "abc123",
          "parsed": {
            "gateway": {
              "reload": {
                "mode": "hybrid"
              }
            }
          }
        }
        """);

        var snapshot = ConfigEditorModel.CaptureSnapshot(document.RootElement);

        Assert.True(snapshot.HasRoot);
        Assert.Equal("abc123", snapshot.BaseHash);
        Assert.Equal("hybrid", snapshot.Root.GetProperty("gateway").GetProperty("reload").GetProperty("mode").GetString());
    }

    [Fact]
    public void ApplyChanges_UpdatesNestedValuesAndPreservesUnrelatedConfig()
    {
        using var document = JsonDocument.Parse("""
        {
          "gateway": {
            "reload": {
              "mode": "hybrid",
              "interval": 5
            }
          },
          "channels": {
            "slack": {
              "enabled": false
            }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["gateway.reload.mode"] = "manual",
                ["gateway.reload.interval"] = 10L,
                ["channels.slack.enabled"] = true,
            });

        Assert.Equal("manual", updated.GetProperty("gateway").GetProperty("reload").GetProperty("mode").GetString());
        Assert.Equal(10, updated.GetProperty("gateway").GetProperty("reload").GetProperty("interval").GetInt64());
        Assert.True(updated.GetProperty("channels").GetProperty("slack").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void ApplyRelativeChanges_OverlaysOnlySelectedSectionDrafts()
    {
        using var document = JsonDocument.Parse("""
        {
          "mode": "hybrid",
          "interval": 5
        }
        """);

        var updated = ConfigEditorModel.ApplyRelativeChanges(
            document.RootElement,
            "gateway.reload",
            new Dictionary<string, object?>
            {
                ["gateway.reload.mode"] = "manual",
                ["gateway.other.value"] = "ignored"
            });

        Assert.Equal("manual", updated.GetProperty("mode").GetString());
        Assert.Equal(5, updated.GetProperty("interval").GetInt32());
        Assert.False(updated.TryGetProperty("other", out _));
    }

    [Fact]
    public void ApplyChanges_AcceptsJsonElementArrayValues()
    {
        using var document = JsonDocument.Parse("""
        {
          "routes": []
        }
        """);
        using var routes = JsonDocument.Parse("""
        [
          { "name": "primary", "enabled": true }
        ]
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["routes"] = routes.RootElement.Clone(),
            });

        var route = updated.GetProperty("routes")[0];
        Assert.Equal("primary", route.GetProperty("name").GetString());
        Assert.True(route.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void ApplyChanges_IgnoresUnsupportedPlainObjectValues()
    {
        using var document = JsonDocument.Parse("""
        {
          "secret": "existing",
          "mode": "hybrid"
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["secret"] = new object(),
                ["mode"] = "manual",
            });

        Assert.Equal("existing", updated.GetProperty("secret").GetString());
        Assert.Equal("manual", updated.GetProperty("mode").GetString());
    }

    [Fact]
    public void SensitiveArray_UnrelatedEdit_PreservesStoredEntries()
    {
        var session = new SensitiveArrayEditSession(2);

        Assert.Equal(SensitiveArrayDecision.Preserve, session.Decision);
        Assert.Null(session.Replacement);
        Assert.Equal("", session.Draft);
        Assert.Equal("2 entries are configured. Stored values stay hidden.", session.CountText);
        Assert.DoesNotContain("stored-secret", session.CountText, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveArray_ReplaceAll_SendsOnlyTheNewArray()
    {
        var session = new SensitiveArrayEditSession(2);
        session.BeginReplace();
        Assert.Equal("", session.Draft);

        session.SetDraft("""[{"url":"https://new.example/hook"}]""");
        Assert.True(session.TryApplyReplace());

        Assert.Equal(SensitiveArrayDecision.Replace, session.Decision);
        var raw = session.Replacement!.Value.GetRawText();
        Assert.Contains("https://new.example/hook", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("stored-secret", raw, StringComparison.Ordinal);
        Assert.Equal("", session.Draft);
    }

    [Fact]
    public void SensitiveArray_ClearAll_SendsAnEmptyArray()
    {
        var session = new SensitiveArrayEditSession(2);
        session.BeginClear();
        Assert.True(session.ClearConfirmOpen);
        Assert.Equal(SensitiveArrayDecision.Preserve, session.Decision);

        session.ConfirmClear();

        Assert.Equal(SensitiveArrayDecision.Clear, session.Decision);
        Assert.Null(session.Replacement);
        Assert.Equal(0, SensitiveArrayEditSession.EmptyArray().GetArrayLength());
    }

    [Fact]
    public void SensitiveArray_CancelReplaceOrClear_KeepsTheStoredArray()
    {
        var session = new SensitiveArrayEditSession(1);
        session.BeginReplace();
        session.SetDraft("""[{"token":"typed-then-cancelled"}]""");
        session.CancelReplace();

        Assert.Equal(SensitiveArrayDecision.Preserve, session.Decision);
        Assert.Equal("", session.Draft);
        Assert.Null(session.Replacement);

        session.BeginClear();
        session.CancelClear();
        Assert.False(session.ClearConfirmOpen);
        Assert.Equal(SensitiveArrayDecision.Preserve, session.Decision);
    }

    [Fact]
    public void SensitiveArray_NearMatchObject_IsNotASecretArrayDecision()
    {
        Assert.False(ConfigPathSensitivity.IsSensitive("channels.googlechat.webhookUrlExtra"));
        Assert.True(ConfigPathSensitivity.IsSensitive("channels.googlechat.webhookUrl"));
    }
}
