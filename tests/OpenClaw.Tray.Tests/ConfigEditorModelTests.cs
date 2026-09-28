using System.Text.Json;
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
    public void FindUneditedRedactionSentinel_OmitsUntouchedRedactedSibling()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": { "signingSecret": "<redacted>", "enabled": true },
            "telegram": { "botToken": "old", "enabled": true }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.telegram.botToken"] = "new-token",
            });

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            new[] { "channels.telegram.botToken" },
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            updated,
            new[] { "channels.telegram.botToken" });

        Assert.Null(blocked);
        Assert.False(sent.GetProperty("channels").GetProperty("slack").TryGetProperty("signingSecret", out _));
        Assert.True(sent.GetProperty("channels").GetProperty("slack").GetProperty("enabled").GetBoolean());
        Assert.Equal("new-token", sent.GetProperty("channels").GetProperty("telegram").GetProperty("botToken").GetString());
    }

    [Fact]
    public void FindUneditedRedactionSentinel_AllowsUserReplacedToken()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "telegram": { "botToken": "<redacted>", "enabled": true }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.telegram.botToken"] = "real-new-token",
            });

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            new[] { "channels.telegram.botToken" },
            document.RootElement);

        Assert.Null(blocked);
        Assert.Equal("real-new-token", updated.GetProperty("channels").GetProperty("telegram").GetProperty("botToken").GetString());
    }

    [Fact]
    public void FindUneditedRedactionSentinel_IgnoresRedactedSubstring()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": { "label": "no redacted secrets here", "enabled": true }
          }
        }
        """);

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            document.RootElement,
            Array.Empty<string>(),
            document.RootElement);

        Assert.Null(blocked);
    }

    [Fact]
    public void FindUneditedRedactionSentinel_BlocksEditedValueThatIsStillTheSentinel()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "telegram": { "botToken": "<redacted>", "enabled": true }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.telegram.botToken"] = "<redacted>",
            });

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            new[] { "channels.telegram.botToken" },
            document.RootElement);

        Assert.Equal("channels.telegram.botToken", blocked);
    }

    [Fact]
    public void FindUneditedRedactionSentinel_OmitsArrayTokenWhenEditUsedTheUnindexedPath()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": { "accounts": [ { "token": "<redacted>" } ] }
          }
        }
        """);

        var edited = new[] { "channels.slack.accounts.token" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            document.RootElement,
            edited,
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            document.RootElement,
            edited);

        Assert.Null(blocked);
        Assert.False(sent.GetProperty("channels").GetProperty("slack").GetProperty("accounts")[0].TryGetProperty("token", out _));
    }

    [Fact]
    public void FindUneditedRedactionSentinel_AllowsLiteralMaskInAnUntouchedArray()
    {
        using var document = JsonDocument.Parse("""
        {
          "labels": ["***", "public"],
          "channels": { "telegram": { "botToken": "old" } }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.telegram.botToken"] = "new-token",
            });

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            new[] { "channels.telegram.botToken" },
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            updated,
            new[] { "channels.telegram.botToken" });

        Assert.Null(blocked);
        Assert.Equal("***", sent.GetProperty("labels")[0].GetString());
        Assert.Equal("new-token", sent.GetProperty("channels").GetProperty("telegram").GetProperty("botToken").GetString());
    }

    [Fact]
    public void FindUneditedRedactionSentinel_AllowsLiteralMaskInANonSecretField()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": { "label": "hello", "enabled": true }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.slack.label"] = "***",
            });

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            new[] { "channels.slack.label" },
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            updated,
            new[] { "channels.slack.label" });

        Assert.Null(blocked);
        Assert.Equal("***", sent.GetProperty("channels").GetProperty("slack").GetProperty("label").GetString());
    }

    [Fact]
    public void FindUneditedRedactionSentinel_OmitsUntouchedArrayApiKeyButKeepsSiblingMask()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "enabled": false,
              "accounts": [ { "label": "<redacted>", "token": "<redacted>", "apiKey": "<redacted>", "api_key": "<redacted>" } ]
            }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.slack.enabled"] = true,
            });
        var edited = new[] { "channels.slack.enabled" };

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            edited,
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(updated, edited);

        Assert.Null(blocked);
        Assert.True(sent.GetProperty("channels").GetProperty("slack").GetProperty("enabled").GetBoolean());
        var account = sent.GetProperty("channels").GetProperty("slack").GetProperty("accounts")[0];
        Assert.False(account.TryGetProperty("token", out _));
        Assert.False(account.TryGetProperty("apiKey", out _));
        Assert.False(account.TryGetProperty("api_key", out _));
        Assert.Equal("<redacted>", account.GetProperty("label").GetString());

        using var siblingOnly = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "accounts": [ { "label": "<redacted>" } ]
            }
          }
        }
        """);

        var siblingBlocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            siblingOnly.RootElement,
            Array.Empty<string>(),
            siblingOnly.RootElement);
        var siblingSent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            siblingOnly.RootElement,
            Array.Empty<string>());

        Assert.Null(siblingBlocked);
        Assert.Equal(
            "<redacted>",
            siblingSent.GetProperty("channels").GetProperty("slack").GetProperty("accounts")[0].GetProperty("label").GetString());

        using var snake = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "accounts": [ { "note": "<redacted>", "api_key": "<redacted>" } ]
            }
          }
        }
        """);

        var snakeBlocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            snake.RootElement,
            Array.Empty<string>(),
            snake.RootElement);
        var snakeSent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            snake.RootElement,
            Array.Empty<string>());

        Assert.Null(snakeBlocked);
        var snakeAccount = snakeSent.GetProperty("channels").GetProperty("slack").GetProperty("accounts")[0];
        Assert.False(snakeAccount.TryGetProperty("api_key", out _));
        Assert.Equal("<redacted>", snakeAccount.GetProperty("note").GetString());
    }

    [Fact]
    public void FindUneditedRedactionSentinel_OmitsUntouchedArrayPrivateKey()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "enabled": false,
              "accounts": [ { "label": "<redacted>", "privateKey": "<redacted>" } ]
            }
          }
        }
        """);

        var updated = ConfigEditorModel.ApplyChanges(
            document.RootElement,
            new Dictionary<string, object?>
            {
                ["channels.slack.enabled"] = true,
            });
        var edited = new[] { "channels.slack.enabled" };

        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            updated,
            edited,
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(updated, edited);

        Assert.Null(blocked);
        Assert.True(sent.GetProperty("channels").GetProperty("slack").GetProperty("enabled").GetBoolean());
        var account = sent.GetProperty("channels").GetProperty("slack").GetProperty("accounts")[0];
        Assert.False(account.TryGetProperty("privateKey", out _));
        Assert.Equal("<redacted>", account.GetProperty("label").GetString());

        using var cased = JsonDocument.Parse("""
        {
          "accounts": [ { "PrivateKey": "<redacted>", "title": "<redacted>" } ]
        }
        """);

        var casedBlocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            cased.RootElement,
            Array.Empty<string>(),
            cased.RootElement);
        var casedSent = ConfigEditorModel.OmitUntouchedRedactionSentinels(
            cased.RootElement,
            Array.Empty<string>());

        Assert.Null(casedBlocked);
        Assert.False(casedSent.GetProperty("accounts")[0].TryGetProperty("PrivateKey", out _));
        Assert.Equal("<redacted>", casedSent.GetProperty("accounts")[0].GetProperty("title").GetString());
    }

    [Fact]
    public void FindUneditedRedactionSentinel_BlocksEditedArrayCredentialThatIsStillTheSentinel()
    {
        using var document = JsonDocument.Parse("""
        {
          "channels": {
            "slack": {
              "accounts": [ { "apiKey": "<redacted>", "privateKey": "<redacted>", "label": "desk" } ]
            }
          }
        }
        """);

        var edited = new[] { "channels.slack.accounts[0].apiKey", "channels.slack.accounts[0].privateKey" };
        var blocked = ConfigEditorModel.FindUneditedRedactionSentinel(
            document.RootElement,
            edited,
            document.RootElement);
        var sent = ConfigEditorModel.OmitUntouchedRedactionSentinels(document.RootElement, edited);

        Assert.Equal("channels.slack.accounts[0].apiKey", blocked);
        var account = sent.GetProperty("channels").GetProperty("slack").GetProperty("accounts")[0];
        Assert.Equal("<redacted>", account.GetProperty("apiKey").GetString());
        Assert.Equal("<redacted>", account.GetProperty("privateKey").GetString());
        Assert.Equal("desk", account.GetProperty("label").GetString());
    }
}
