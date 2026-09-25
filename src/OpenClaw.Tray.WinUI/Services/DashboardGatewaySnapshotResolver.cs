using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal sealed record DashboardGatewaySnapshot(
    GatewayRecord Record,
    bool IsRegistryRecord,
    string? LegacyPrimary,
    string? LegacyBootstrap);

internal sealed class DashboardGatewaySnapshotResolver
{
    private readonly GatewayRegistry? _registry;
    private readonly SettingsManager _settings;
    private readonly string _legacyIdentityDirectory;

    public DashboardGatewaySnapshotResolver(
        GatewayRegistry? registry,
        SettingsManager settings,
        string legacyIdentityDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyIdentityDirectory);
        _registry = registry;
        _settings = settings;
        _legacyIdentityDirectory = legacyIdentityDirectory;
    }

    public bool TryCapture(out DashboardGatewaySnapshot? snapshot)
    {
        if (_registry?.GetActive() is { } active)
        {
            snapshot = new DashboardGatewaySnapshot(
                active,
                IsRegistryRecord: true,
                LegacyPrimary: null,
                LegacyBootstrap: null);
            return true;
        }

        var gatewayUrl = _settings.GetEffectiveGatewayUrl();
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            snapshot = null;
            return false;
        }

        SshTunnelConfig? tunnel = null;
        if (_settings.UseSshTunnel)
        {
            tunnel = new SshTunnelConfig(
                _settings.SshTunnelUser ?? "",
                _settings.SshTunnelHost ?? "",
                _settings.SshTunnelRemotePort,
                _settings.SshTunnelLocalPort,
                BrowserProxySshTunnelForwardPolicy.ShouldInclude(
                    _settings.NodeBrowserProxyEnabled,
                    _settings.SshTunnelRemotePort,
                    _settings.SshTunnelLocalPort),
                _settings.SshTunnelSshPort);
        }

        snapshot = new DashboardGatewaySnapshot(
            new GatewayRecord
            {
                Id = "legacy-settings",
                Url = gatewayUrl,
                IsLocal = GatewayRecordEditing.IsLoopbackEndpoint(gatewayUrl),
                SshTunnel = tunnel,
            },
            false,
            _settings.LegacyToken,
            _settings.LegacyBootstrapToken);
        return true;
    }

    public bool IsCurrent(DashboardGatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.IsRegistryRecord)
        {
            return _registry?.GetActive() is { } active &&
                RecordsMatch(snapshot.Record, active);
        }

        return _registry?.GetActive() == null &&
            TryCapture(out var current) &&
            current is { IsRegistryRecord: false } &&
            RecordsMatch(snapshot.Record, current.Record) &&
            string.Equals(
                snapshot.LegacyPrimary,
                current.LegacyPrimary,
                StringComparison.Ordinal) &&
            string.Equals(
                snapshot.LegacyBootstrap,
                current.LegacyBootstrap,
                StringComparison.Ordinal);
    }

    public InteractiveGatewayCredential? ResolveCredentials(
        DashboardGatewaySnapshot snapshot,
        IDeviceIdentityReader identityReader,
        Func<GatewayRecord, GatewayCredential, bool>? authorizeCredential)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(identityReader);
        if (!snapshot.IsRegistryRecord)
        {
            return InteractiveGatewayCredentialResolver.TryResolve(
                null,
                _legacyIdentityDirectory,
                identityReader,
                snapshot.Record.Url,
                snapshot.LegacyPrimary,
                snapshot.LegacyBootstrap,
                authorizeCredential,
                out var legacyResult)
                    ? legacyResult
                    : null;
        }

        var record = snapshot.Record;
        var identityDirectory = snapshot.IsRegistryRecord && _registry != null
            ? _registry.GetIdentityDirectory(snapshot.Record.Id)
            : _legacyIdentityDirectory;
        GatewayCredential? resolved;
        if (!string.IsNullOrWhiteSpace(record.SharedGatewayToken))
        {
            resolved = new GatewayCredential(
                record.SharedGatewayToken!,
                IsBootstrapToken: false,
                CredentialResolver.SourceSharedGatewayToken);
        }
        else
        {
            resolved = new CredentialResolver(identityReader)
                .ResolveOperator(record, identityDirectory);
        }

        if (resolved == null ||
            (authorizeCredential is not null && !authorizeCredential(record, resolved)))
        {
            return null;
        }

        string gatewayUrl;
        try
        {
            gatewayUrl = GatewayClientEndpointResolver.Resolve(record);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return new InteractiveGatewayCredential(
            gatewayUrl,
            resolved.Token,
            resolved.IsBootstrapToken,
            resolved.Source);
    }

    internal static bool RecordsMatch(
        GatewayRecord expected,
        GatewayRecord current) =>
        string.Equals(expected.Id, current.Id, StringComparison.Ordinal) &&
        string.Equals(expected.Url, current.Url, StringComparison.Ordinal) &&
        string.Equals(
            expected.SharedGatewayToken,
            current.SharedGatewayToken,
            StringComparison.Ordinal) &&
        string.Equals(
            expected.BootstrapToken,
            current.BootstrapToken,
            StringComparison.Ordinal) &&
        expected.IsLocal == current.IsLocal &&
        expected.RequiresV2Signature == current.RequiresV2Signature &&
        string.Equals(
            expected.SetupManagedDistroName,
            current.SetupManagedDistroName,
            StringComparison.Ordinal) &&
        Equals(expected.SshTunnel, current.SshTunnel);
}
