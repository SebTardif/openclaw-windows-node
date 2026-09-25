using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class DashboardGatewaySnapshotResolverTests
{
    [Fact]
    public void ActiveSshRecord_ResolvesForwardAndDetectsSecurityRelevantChange()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        var record = new GatewayRecord
        {
            Id = "gateway-1",
            Url = "wss://gateway.example",
            SshTunnel = new SshTunnelConfig("user", "ssh.example", 18789, 45678),
            SharedGatewayToken = "test-auth-token",
        };
        registry.AddOrUpdate(record);
        registry.SetActive(record.Id);
        var resolver = new DashboardGatewaySnapshotResolver(
            registry,
            settings,
            temp.Path);

        Assert.True(resolver.TryCapture(out var snapshot));
        Assert.NotNull(snapshot);
        Assert.True(resolver.IsCurrent(snapshot!));
        var result = resolver.ResolveCredentials(
            snapshot!,
            new EmptyIdentityReader(),
            authorizeCredential: (_, _) => true);
        Assert.NotNull(result);
        Assert.Equal("ws://localhost:45678", result!.GatewayUrl);
        Assert.Equal("test-auth-token", result.Token);

        registry.AddOrUpdate(record with { SharedGatewayToken = "secret-token" });

        Assert.False(resolver.IsCurrent(snapshot));
    }

    [Fact]
    public void RecordsMatch_IgnoresPresentationOnlyChanges()
    {
        var expected = new GatewayRecord
        {
            Id = "gateway-1",
            Url = "wss://gateway.example",
            FriendlyName = "Original",
            LastConnected = DateTime.UtcNow.AddDays(-1),
            SharedGatewayToken = "test-auth-token",
        };
        var current = expected with
        {
            FriendlyName = "Renamed",
            LastConnected = DateTime.UtcNow,
        };

        Assert.True(DashboardGatewaySnapshotResolver.RecordsMatch(expected, current));
    }

    private sealed class EmptyIdentityReader : IDeviceIdentityReader
    {
        public string? TryReadStoredDeviceToken(string dataPath) => null;

        public string? TryReadStoredNodeDeviceToken(string dataPath) => null;
    }
}
