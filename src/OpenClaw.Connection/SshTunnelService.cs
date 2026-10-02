using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;

namespace OpenClaw.Connection;

/// <summary>
/// SSH process that passed listener ownership for one dashboard launch.
/// </summary>
public readonly record struct SettingsOwnedForwardBinding(
    bool Owned,
    long Generation,
    int LocalPort,
    int ProcessId);

/// <summary>
/// Manages an SSH local port-forward process for gateway access.
/// </summary>
public sealed class SshTunnelService : ISshTunnelManager
{
    private readonly IOpenClawLogger _logger;
    private readonly object _operationLock = new();
    private readonly object _stateLock = new();
    private Process? _process;
    private bool _processStarted;
    private SshTunnelConfig? _currentConfig;
    private SshTunnelOwner _currentOwner;
    private string? _lastSpec;
    private long _lifecycleGeneration;
    private int _browserHandoffLeases;
    private readonly List<BrowserHandoffLease> _browserHandoffs = new();
    private readonly HashSet<string> _claimedBrowserConnections = new(StringComparer.Ordinal);
    private long _nextBrowserHandoffId;
    private DeferredTunnelStop _deferredStop;

    /// <summary>Raised when the SSH tunnel exits unexpectedly (not during shutdown).</summary>
    public event EventHandler<SshTunnelExit>? TunnelExited;

    public SshTunnelService(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return IsRunningLocked();
            }
        }
    }

    public bool IsActive => IsRunning;
    public long OwnershipGeneration
    {
        get
        {
            lock (_stateLock)
            {
                return _lifecycleGeneration;
            }
        }
    }

    public bool HasDeferredStop
    {
        get
        {
            lock (_stateLock)
            {
                return _deferredStop != DeferredTunnelStop.None;
            }
        }
    }

    public int BrowserHandoffLeaseCount
    {
        get
        {
            lock (_stateLock)
            {
                return _browserHandoffLeases;
            }
        }
    }

    public SshTunnelConfig? ActiveConfig
    {
        get
        {
            lock (_stateLock)
            {
                return IsRunningLocked() ? _currentConfig : null;
            }
        }
    }
    public string? LocalTunnelUrl => IsActive ? $"ws://localhost:{CurrentLocalPort}" : null;
    public string? CurrentUser { get; private set; }
    public string? CurrentHost { get; private set; }
    public int CurrentRemotePort { get; private set; }
    public int CurrentLocalPort { get; private set; }
    public int CurrentBrowserProxyRemotePort { get; private set; }
    public int CurrentBrowserProxyLocalPort { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public TunnelStatus Status { get; private set; } = TunnelStatus.NotConfigured;

    public SshTunnelSnapshot CreateSnapshot()
    {
        lock (_stateLock)
        {
            return new SshTunnelSnapshot(
                IsRunningLocked(),
                CurrentUser,
                CurrentHost,
                CurrentRemotePort,
                CurrentLocalPort,
                CurrentBrowserProxyRemotePort,
                CurrentBrowserProxyLocalPort,
                StartedAtUtc,
                LastError,
                Status);
        }
    }

    public bool TryMarkRestarting(SshTunnelExit tunnelExit)
    {
        lock (_stateLock)
        {
            if (tunnelExit.Generation != _lifecycleGeneration ||
                !Equals(_currentConfig, tunnelExit.Tunnel) ||
                IsRunningLocked() ||
                Status != TunnelStatus.Failed)
            {
                return false;
            }

            MarkRestartingLocked(tunnelExit.ExitCode);
            return true;
        }
    }

    public void MarkRestarting(int exitCode)
    {
        lock (_stateLock)
        {
            MarkRestartingLocked(exitCode);
        }
    }

    public bool IsRestartPending(SshTunnelExit tunnelExit)
    {
        lock (_stateLock)
        {
            return tunnelExit.Generation == _lifecycleGeneration &&
                   Equals(_currentConfig, tunnelExit.Tunnel) &&
                   tunnelExit.Owner == _currentOwner &&
                   !IsRunningLocked() &&
                   Status == TunnelStatus.Restarting;
        }
    }

    public bool TryMarkRecoveryFailed(SshTunnelExit tunnelExit, string reason)
    {
        lock (_stateLock)
        {
            if (!IsRestartPendingLocked(tunnelExit))
                return false;

            Status = TunnelStatus.Failed;
            LastError = reason;
            return true;
        }
    }

    public bool TryRestart(SshTunnelExit tunnelExit)
    {
        lock (_operationLock)
        {
            lock (_stateLock)
            {
                if (!IsRestartPendingLocked(tunnelExit))
                    return false;
            }

            EnsureStartedCore(tunnelExit.Tunnel, tunnelExit.Owner);
            return true;
        }
    }

    public void EnsureStarted(string user, string host, int remotePort, int localPort)
        => EnsureStarted(user, host, remotePort, localPort, includeBrowserProxyForward: false);

    public void EnsureStarted(string user, string host, int remotePort, int localPort, bool includeBrowserProxyForward)
        => EnsureStarted(user, host, remotePort, localPort, includeBrowserProxyForward, sshPort: 22);

    public void EnsureStarted(string user, string host, int remotePort, int localPort, bool includeBrowserProxyForward, int sshPort)
        => EnsureStartedCore(
            new SshTunnelConfig(user, host, remotePort, localPort, includeBrowserProxyForward, sshPort),
            SshTunnelOwner.Settings);

    private void EnsureStartedCore(
        SshTunnelConfig tunnel,
        SshTunnelOwner owner,
        Action<SshTunnelConfig>? beforeStart = null)
    {
        lock (_operationLock)
        {
            var user = tunnel.User.Trim();
            var host = tunnel.Host.Trim();
            tunnel = tunnel with { User = user, Host = host };

            var spec = BuildSpec(
                user,
                host,
                tunnel.RemotePort,
                tunnel.LocalPort,
                tunnel.IncludeBrowserProxyForward,
                tunnel.SshPort);

            Process? claimedProcess;
            lock (_stateLock)
            {
                if (IsRunningLocked() && string.Equals(_lastSpec, spec, StringComparison.Ordinal))
                {
                    _currentOwner = ResolveOwnerForReuse(_currentOwner, owner);
                    Status = TunnelStatus.Up;
                    return;
                }

                if (_browserHandoffLeases > 0)
                    throw new InvalidOperationException("SSH tunnel is held for a dashboard launch.");

                // Lease check and claim share this hold. A later handoff cannot
                // make the stop defer after this caller has decided to replace.
                claimedProcess = ClaimProcessForStopLocked();
            }

            StopClaimedProcess(claimedProcess);
            beforeStart?.Invoke(tunnel);
            lock (_stateLock)
            {
                Status = TunnelStatus.Starting;
            }
            StartProcess(tunnel, owner, spec);
        }
    }

    public void Stop()
    {
        lock (_operationLock)
        {
            StopLocked();
        }
    }

    /// <summary>
    /// Stops the tracked tunnel. Returns false when a browser handoff lease
    /// defers the stop and leaves the process, spec, owner, and status unchanged.
    /// The deferred stop completes on the last <see cref="ExitBrowserHandoff"/>.
    /// </summary>
    private bool StopLocked()
    {
        Process? process;
        lock (_stateLock)
        {
            if (_browserHandoffLeases > 0)
            {
                RememberDeferredStopLocked(DeferredTunnelStop.Stop);
                return false;
            }

            process = ClaimProcessForStopLocked();
            _deferredStop = DeferredTunnelStop.None;
        }

        StopClaimedProcess(process);
        return true;
    }

    /// <summary>
    /// Caller holds <see cref="_stateLock"/> and has already decided the lease
    /// does not defer this stop. Does not consult the lease again.
    /// </summary>
    private Process? ClaimProcessForStopLocked()
    {
        // Claim and clear the current process before stopping it. Exit callbacks can
        // then only observe stale ownership and cannot overwrite a replacement.
        _lifecycleGeneration++;
        var process = _process;
        _process = null;
        _processStarted = false;
        _currentConfig = null;
        _currentOwner = SshTunnelOwner.Unspecified;
        _lastSpec = null;
        CurrentBrowserProxyLocalPort = 0;
        CurrentBrowserProxyRemotePort = 0;
        StartedAtUtc = null;
        if (Status != TunnelStatus.NotConfigured)
            Status = TunnelStatus.Stopped;
        return process;
    }

    private void StopClaimedProcess(Process? process)
    {
        if (process == null)
            return;

        _logger.Info("Stopping SSH tunnel process");
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"SSH tunnel stop failed: {ex.Message}");
        }
        finally
        {
            try { process.Dispose(); }
            catch (Exception disposeEx) { _logger.Debug($"SshTunnelService.Stop: process dispose failed: {disposeEx.Message}"); }
        }
    }

    public void ResetNotConfigured()
    {
        lock (_operationLock)
        {
            Process? process = null;
            var deferred = false;
            lock (_stateLock)
            {
                if (_browserHandoffLeases > 0)
                {
                    RememberDeferredStopLocked(DeferredTunnelStop.ResetNotConfigured);
                    deferred = true;
                }
                else
                {
                    process = ClaimProcessForStopLocked();
                    _deferredStop = DeferredTunnelStop.None;
                    LastError = null;
                    Status = TunnelStatus.NotConfigured;
                }
            }

            if (!deferred)
                StopClaimedProcess(process);
        }
    }

    private void StartProcess(SshTunnelConfig tunnel, SshTunnelOwner owner, string spec)
    {
        var user = tunnel.User;
        var host = tunnel.Host;
        var remotePort = tunnel.RemotePort;
        var localPort = tunnel.LocalPort;
        var includeBrowserProxyForward = tunnel.IncludeBrowserProxyForward;
        var sshPort = tunnel.SshPort;
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = SshTunnelCommandLine.BuildArguments(
                user,
                host,
                remotePort,
                localPort,
                includeBrowserProxyForward,
                sshPort),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = new Process
        {
            StartInfo = psi,
        };
        long generation = 0;

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.Info($"[SSH] {e.Data}");
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.Warn($"[SSH] {e.Data}");
            }
        };

        process.Exited += (_, _) =>
        {
            SshTunnelExit? tunnelExit = null;
            lock (_stateLock)
            {
                if (generation == _lifecycleGeneration &&
                    ReferenceEquals(_process, process))
                {
                    int exitCode;
                    try
                    {
                        exitCode = process.ExitCode;
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"Ignoring SSH tunnel exit after process disposal: {ex.Message}");
                        return;
                    }

                    LastError = $"SSH tunnel exited unexpectedly with code {exitCode}.";
                    StartedAtUtc = null;
                    Status = TunnelStatus.Failed;
                    _process = null;
                    _processStarted = false;
                    _lastSpec = null;
                    CurrentBrowserProxyLocalPort = 0;
                    CurrentBrowserProxyRemotePort = 0;
                    tunnelExit = new SshTunnelExit(exitCode, tunnel, generation, _currentOwner);
                }
            }

            if (tunnelExit == null)
            {
                _logger.Debug("Ignoring stale SSH tunnel exit");
                return;
            }

            _logger.Warn($"SSH tunnel exited unexpectedly (code {tunnelExit.ExitCode})");
            try { process.Dispose(); }
            catch (Exception disposeEx) { _logger.Debug($"SshTunnelService: process dispose after unexpected exit failed: {disposeEx.Message}"); }
            TunnelExited?.Invoke(this, tunnelExit);
        };

        lock (_stateLock)
        {
            generation = ++_lifecycleGeneration;
            _process = process;
            _processStarted = false;
            _currentConfig = tunnel;
            _currentOwner = owner;
            _lastSpec = spec;
        }

        var processStarted = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start ssh process");
            }
            processStarted = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_stateLock)
            {
                if (generation != _lifecycleGeneration ||
                    !ReferenceEquals(_process, process))
                {
                    return;
                }

                _processStarted = true;
                CurrentUser = user;
                CurrentHost = host;
                CurrentRemotePort = remotePort;
                CurrentLocalPort = localPort;
                CurrentBrowserProxyRemotePort = includeBrowserProxyForward ? remotePort + 2 : 0;
                CurrentBrowserProxyLocalPort = includeBrowserProxyForward ? localPort + 2 : 0;
                StartedAtUtc = DateTime.UtcNow;
                LastError = null;
                Status = TunnelStatus.Up;
            }

            // Enable exit delivery only after the process is fully published. If it already
            // exited, Process raises the event now and the callback atomically claims it.
            process.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                if (generation == _lifecycleGeneration &&
                    ReferenceEquals(_process, process))
                {
                    LastError = ex.Message;
                    Status = TunnelStatus.Failed;
                    _process = null;
                    _processStarted = false;
                    _currentConfig = null;
                    _currentOwner = SshTunnelOwner.Unspecified;
                    _lastSpec = null;
                }
            }
            if (processStarted)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception killEx)
                {
                    _logger.Debug($"SshTunnelService: process cleanup after start failure failed: {killEx.Message}");
                }
            }
            process.Dispose();
            throw new InvalidOperationException("Unable to start SSH tunnel process. Ensure OpenSSH client is installed and available in PATH.", ex);
        }

        lock (_stateLock)
        {
            if (generation != _lifecycleGeneration ||
                !ReferenceEquals(_process, process))
            {
                return;
            }
        }

        _logger.Info($"SSH tunnel started: 127.0.0.1:{localPort} -> 127.0.0.1:{remotePort} via {user}@{host}:{sshPort}");
        if (includeBrowserProxyForward)
        {
            _logger.Info($"SSH tunnel browser proxy forward started: 127.0.0.1:{localPort + 2} -> 127.0.0.1:{remotePort + 2} via {user}@{host}:{sshPort}");
        }
    }

    private bool IsRunningLocked() => _processStarted && _process is { HasExited: false };

    private bool IsRestartPendingLocked(SshTunnelExit tunnelExit) =>
        tunnelExit.Generation == _lifecycleGeneration &&
        Equals(_currentConfig, tunnelExit.Tunnel) &&
        tunnelExit.Owner == _currentOwner &&
        !IsRunningLocked() &&
        Status == TunnelStatus.Restarting;

    internal static SshTunnelOwner ResolveOwnerForReuse(
        SshTunnelOwner currentOwner,
        SshTunnelOwner requestedOwner) =>
        currentOwner == SshTunnelOwner.GatewayConnectionManager
            ? currentOwner
            : requestedOwner;

    private void MarkRestartingLocked(int exitCode)
    {
        Status = TunnelStatus.Restarting;
        LastError = $"SSH tunnel exited unexpectedly with code {exitCode}; restart is scheduled.";
    }

    private static string BuildSpec(string user, string host, int remotePort, int localPort, bool includeBrowserProxyForward, int sshPort)
        => $"{user}@{host}:{sshPort}:{localPort}:{remotePort}:browserProxy={includeBrowserProxyForward}";

    public void Dispose()
    {
        Stop();
    }

    public Task<bool> IsOwnedListenerReadyAsync(
        SshTunnelConfig config,
        int destinationPort,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var isConfiguredForward =
            destinationPort == config.LocalPort ||
            (config.IncludeBrowserProxyForward && destinationPort == config.LocalPort + 2);
        if (!isConfiguredForward)
            return Task.FromResult(false);

        var normalizedConfig = config with
        {
            User = config.User.Trim(),
            Host = config.Host.Trim(),
        };

        Process process;
        long generation;
        int processId;
        DateTime processStartTimeUtc;
        lock (_stateLock)
        {
            if (!IsRunningLocked() ||
                _process is null ||
                !Equals(_currentConfig, normalizedConfig) ||
                _currentOwner != SshTunnelOwner.GatewayConnectionManager)
            {
                return Task.FromResult(false);
            }

            process = _process;
            generation = _lifecycleGeneration;
            processId = process.Id;
            try
            {
                processStartTimeUtc = process.StartTime.ToUniversalTime();
            }
            catch (Exception ex)
            {
                _logger.Debug($"SSH listener ownership process inspection failed: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        try
        {
            if (!ValidateListenerOwnership(
                    WindowsTcpListenerSnapshot.Capture(),
                    destinationPort,
                     processId,
                     processStartTimeUtc))
            {
                return Task.FromResult(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"SSH listener ownership verification failed: {ex.Message}");
            return Task.FromResult(false);
        }

        lock (_stateLock)
        {
            return Task.FromResult(
                generation == _lifecycleGeneration &&
                ReferenceEquals(_process, process) &&
                IsRunningLocked() &&
                Equals(_currentConfig, normalizedConfig) &&
                _currentOwner == SshTunnelOwner.GatewayConnectionManager);
        }
    }

    public async Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct) =>
        (await StartOwnedAsync(config, ct).ConfigureAwait(false)).Url;

    public async Task<SettingsOwnedForwardBinding> EnsureSettingsOwnedForwardReadyAsync(
        SshTunnelConfig config,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        long generation = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureStartedCore(
                config,
                SshTunnelOwner.Settings,
                tunnel => RejectOccupiedForwardPorts(tunnel));

            var normalizedConfig = config with
            {
                User = config.User.Trim(),
                Host = config.Host.Trim(),
            };
            DateTime processStartTimeUtc;
            lock (_stateLock)
            {
                if (!IsRunningLocked() ||
                    _process is null ||
                    !Equals(_currentConfig, normalizedConfig))
                {
                    throw new InvalidOperationException(
                        "SSH tunnel changed before listener ownership could be verified.");
                }

                process = _process;
                generation = _lifecycleGeneration;
                processStartTimeUtc = process.StartTime.ToUniversalTime();
            }

            var processId = process.Id;
            await WaitForOwnedLocalListenerAsync(
                config.LocalPort,
                process,
                generation,
                processId,
                processStartTimeUtc,
                cancellationToken).ConfigureAwait(false);
            if (config.IncludeBrowserProxyForward)
            {
                await WaitForOwnedLocalListenerAsync(
                    config.LocalPort + 2,
                    process,
                    generation,
                    processId,
                    processStartTimeUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            lock (_stateLock)
            {
                if (generation != _lifecycleGeneration ||
                    !ReferenceEquals(_process, process) ||
                    !IsRunningLocked() ||
                    !Equals(_currentConfig, normalizedConfig) ||
                    _currentOwner is not (SshTunnelOwner.Settings or SshTunnelOwner.GatewayConnectionManager))
                {
                    return default;
                }

                return new SettingsOwnedForwardBinding(
                    true,
                    generation,
                    normalizedConfig.LocalPort,
                    processId);
            }
        }
        catch (Exception ex)
        {
            var stillCurrent = false;
            lock (_stateLock)
            {
                stillCurrent = process is null
                    ? _process is null
                    : generation == _lifecycleGeneration && ReferenceEquals(_process, process);
                if (!stillCurrent)
                {
                    _logger.Warn($"SSH dashboard forward wait lost ownership: {ex.Message}");
                    return default;
                }

                LastError = ex.Message;
                Status = TunnelStatus.Failed;
            }

            if (process is not null)
                StopIfCurrent(process, generation);

            _logger.Warn($"SSH dashboard forward is not owned: {ex.Message}");
            return default;
        }
    }

    public bool IsSettingsOwnedForwardCurrent(long generation, int localPort)
    {
        lock (_stateLock)
        {
            return IsForwardCurrentLocked(generation, localPort);
        }
    }

    public bool TryEnterBrowserHandoff(long generation, int localPort, int processId = 0) =>
        TryEnterBrowserHandoff(generation, localPort, processId, out _);

    public bool TryEnterBrowserHandoff(long generation, int localPort, int processId, out long handoffId)
    {
        lock (_stateLock)
        {
            handoffId = 0;
            if (!IsForwardCurrentLocked(generation, localPort))
                return false;
            if (processId > 0 && _process?.Id != processId)
                return false;

            handoffId = ++_nextBrowserHandoffId;
            _browserHandoffLeases++;
            _browserHandoffs.Add(new BrowserHandoffLease
            {
                Id = handoffId,
                LocalPort = localPort,
                Seen = WindowsTcpListenerSnapshot.TryCollectEstablishedForwardKeys(localPort, out var seen)
                    ? seen
                    : null,
            });
            return true;
        }
    }

    public void NoteBrowserHandoffClient(int processId) =>
        NoteBrowserHandoffClient(NewestOpenHandoffId(), processId);

    public void NoteBrowserHandoffClient(long handoffId, int processId)
    {
        if (handoffId <= 0 || processId <= 0)
            return;

        lock (_stateLock)
        {
            var handoff = FindOpenHandoffLocked(handoffId);
            if (handoff is not null)
                handoff.BrowserProcessId = processId;
        }
    }

    public void NoteBrowserHandoffProcessName(long handoffId, string processName)
    {
        if (handoffId <= 0 || string.IsNullOrWhiteSpace(processName))
            return;

        lock (_stateLock)
        {
            var handoff = FindOpenHandoffLocked(handoffId);
            if (handoff is not null)
                handoff.BrowserProcessName = processName;
        }
    }

    internal int? HandoffBrowserProcessId(long handoffId)
    {
        lock (_stateLock)
        {
            return FindOpenHandoffLocked(handoffId)?.BrowserProcessId;
        }
    }

    public bool IsBrowserHandoffOpen(long handoffId)
    {
        lock (_stateLock)
        {
            return FindOpenHandoffLocked(handoffId) is not null;
        }
    }

    /// <summary>
    /// Keeps one browser-handoff lease after launch and asks the caller to watch it.
    /// A stop that arrives before attributable browser use stays deferred.
    /// Returns false when <paramref name="localPort"/> is not leased.
    /// </summary>
    public bool TryReleaseBrowserHandoffUnlessDeferred(int localPort, out bool watchDeferredStop)
    {
        lock (_stateLock)
        {
            watchDeferredStop = false;
            if (_browserHandoffLeases <= 0 || _currentConfig?.LocalPort != localPort)
                return false;

            // Process.Start returns before the browser connects. Keep the lease
            // until attributable use, including a stop that arrives after launch.
            watchDeferredStop = true;
            return true;
        }
    }

    public void ExitBrowserHandoff()
    {
        long handoffId;
        lock (_stateLock)
        {
            var oldest = FindOldestOpenHandoffLocked();
            if (oldest is null)
                return;
            handoffId = oldest.Id;
        }

        ExitBrowserHandoff(handoffId);
    }

    public void ExitBrowserHandoff(long handoffId)
    {
        // Same order as Stop: operation lock, then state lock. Claim under the
        // state lock and kill only after releasing it. Do not take the operation
        // lock again when the caller already holds it.
        var enteredOperationLock = false;
        if (!Monitor.IsEntered(_operationLock))
        {
            Monitor.Enter(_operationLock);
            enteredOperationLock = true;
        }

        try
        {
            Process? process = null;
            var completeStop = false;
            lock (_stateLock)
            {
                var handoff = _browserHandoffs.FirstOrDefault(item => item.Id == handoffId);
                if (handoff is null || handoff.Settled)
                    return;

                handoff.Settled = true;
                _browserHandoffs.Remove(handoff);
                if (_browserHandoffLeases > 0)
                    _browserHandoffLeases--;
                if (_browserHandoffLeases == 0)
                    _claimedBrowserConnections.Clear();

                if (_browserHandoffLeases != 0 || _deferredStop == DeferredTunnelStop.None)
                    return;

                var reset = _deferredStop == DeferredTunnelStop.ResetNotConfigured;
                process = ClaimProcessForStopLocked();
                _deferredStop = DeferredTunnelStop.None;
                if (reset)
                {
                    LastError = null;
                    Status = TunnelStatus.NotConfigured;
                }

                completeStop = true;
            }

            if (completeStop)
                StopClaimedProcess(process);
        }
        finally
        {
            if (enteredOperationLock)
                Monitor.Exit(_operationLock);
        }
    }

    /// <summary>
    /// Releases one browser-handoff lease for <paramref name="localPort"/>.
    /// A deferred stop or reset then runs. Returns false when that port is not leased.
    /// </summary>
    public bool TryCompleteDeferredBrowserHandoff(int localPort)
    {
        var enteredOperationLock = false;
        if (!Monitor.IsEntered(_operationLock))
        {
            Monitor.Enter(_operationLock);
            enteredOperationLock = true;
        }

        try
        {
            lock (_stateLock)
            {
                if (_browserHandoffLeases <= 0 || _currentConfig?.LocalPort != localPort)
                    return false;
            }

            ExitBrowserHandoff();
            return true;
        }
        finally
        {
            if (enteredOperationLock)
                Monitor.Exit(_operationLock);
        }
    }

    /// <summary>
    /// Waits until the browser uses <paramref name="localPort"/>, or the SSH process is gone.
    /// A timeout leaves the lease and listener in place and returns false.
    /// </summary>
    public Task<bool> WatchBrowserHandoffConsumptionAsync(
        int localPort,
        TimeSpan timeout,
        Func<int, bool>? consumptionProbe,
        CancellationToken cancellationToken = default) =>
        WatchBrowserHandoffConsumptionAsync(0, localPort, timeout, consumptionProbe, cancellationToken);

    public async Task<bool> WatchBrowserHandoffConsumptionAsync(
        long handoffId,
        int localPort,
        TimeSpan timeout,
        Func<int, bool>? consumptionProbe,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (handoffId > 0)
            {
                if (!IsBrowserHandoffOpen(handoffId))
                    return true;
            }
            else if (BrowserHandoffLeaseCount <= 0)
            {
                return true;
            }

            var consumed = consumptionProbe != null
                ? consumptionProbe(localPort)
                : handoffId > 0 && TryClaimHandoffConsumption(handoffId, localPort);
            if (consumed || !IsRunning)
            {
                var completed = handoffId > 0
                    ? TryFinishHandoff(handoffId, localPort)
                    : TryCompleteDeferredBrowserHandoff(localPort);
                if (completed || (handoffId > 0 ? !IsBrowserHandoffOpen(handoffId) : BrowserHandoffLeaseCount <= 0))
                    return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                _logger.Warn(
                    "Dashboard SSH forward stayed owned because the browser has not connected; the listener was not stopped.");
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryFinishHandoff(long handoffId, int localPort)
    {
        lock (_stateLock)
        {
            var handoff = FindOpenHandoffLocked(handoffId);
            if (handoff is null || handoff.LocalPort != localPort)
                return false;
        }

        ExitBrowserHandoff(handoffId);
        return true;
    }

    private bool TryClaimHandoffConsumption(long handoffId, int localPort)
    {
        var clients = WindowsTcpListenerSnapshot.ListEstablishedForwardClients(localPort);
        List<BrowserHandoffView> views;
        HashSet<string> claimed;
        lock (_stateLock)
        {
            views = _browserHandoffs
                .Select(handoff => new BrowserHandoffView(
                    handoff.Id,
                    handoff.Settled,
                    handoff.BrowserProcessId,
                    handoff.BrowserProcessName,
                    handoff.Seen))
                .ToList();
            claimed = new HashSet<string>(_claimedBrowserConnections, StringComparer.Ordinal);
        }

        var rows = new List<ForwardClientRow>(clients.Count);
        foreach (var client in clients)
            rows.Add(new ForwardClientRow(client.Key, client.ProcessId, ProcessNameFor(client.ProcessId)));

        if (!BrowserHandoffConsumption.TrySelectExclusiveConsumption(handoffId, views, rows, claimed, out var key) ||
            key is null)
        {
            return false;
        }

        lock (_stateLock)
        {
            if (_claimedBrowserConnections.Contains(key) || FindOpenHandoffLocked(handoffId) is null)
                return false;

            _claimedBrowserConnections.Add(key);
            return true;
        }
    }

    private static string? ProcessNameFor(int processId)
    {
        if (processId <= 0)
            return null;

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private long NewestOpenHandoffId()
    {
        lock (_stateLock)
        {
            return FindNewestOpenHandoffLocked()?.Id ?? 0;
        }
    }

    private BrowserHandoffLease? FindOpenHandoffLocked(long handoffId) =>
        _browserHandoffs.FirstOrDefault(handoff => handoff.Id == handoffId && !handoff.Settled);

    private BrowserHandoffLease? FindOldestOpenHandoffLocked()
    {
        BrowserHandoffLease? oldest = null;
        foreach (var handoff in _browserHandoffs)
        {
            if (handoff.Settled)
                continue;
            if (oldest is null || handoff.Id < oldest.Id)
                oldest = handoff;
        }

        return oldest;
    }

    private BrowserHandoffLease? FindNewestOpenHandoffLocked()
    {
        BrowserHandoffLease? newest = null;
        foreach (var handoff in _browserHandoffs)
        {
            if (handoff.Settled)
                continue;
            if (newest is null || handoff.Id > newest.Id)
                newest = handoff;
        }

        return newest;
    }

    private sealed class BrowserHandoffLease
    {
        public long Id { get; init; }
        public int LocalPort { get; init; }
        public int? BrowserProcessId { get; set; }
        public string? BrowserProcessName { get; set; }
        public HashSet<string>? Seen { get; init; }
        public bool Settled { get; set; }
    }

    /// <summary>
    /// Caller holds <see cref="_stateLock"/>. A reset replaces a plain stop.
    /// A later stop does not downgrade a reset.
    /// </summary>
    private void RememberDeferredStopLocked(DeferredTunnelStop kind)
    {
        if (_deferredStop != DeferredTunnelStop.ResetNotConfigured)
            _deferredStop = kind;
    }

    private enum DeferredTunnelStop
    {
        None,
        Stop,
        ResetNotConfigured,
    }

    private bool IsForwardCurrentLocked(long generation, int localPort)
    {
        return generation == _lifecycleGeneration &&
            IsRunningLocked() &&
            _currentOwner is SshTunnelOwner.Settings or SshTunnelOwner.GatewayConnectionManager &&
            _currentConfig?.LocalPort == localPort;
    }

    private static void RejectOccupiedForwardPorts(SshTunnelConfig tunnel)
    {
        EnsurePortIsUnoccupied(WindowsTcpListenerSnapshot.Capture(), tunnel.LocalPort);
        if (tunnel.IncludeBrowserProxyForward)
            EnsurePortIsUnoccupied(WindowsTcpListenerSnapshot.Capture(), tunnel.LocalPort + 2);
    }

    public async Task<SshTunnelStartResult> StartOwnedAsync(
        SshTunnelConfig config,
        CancellationToken ct)
    {
        Process? process = null;
        long generation = 0;
        try
        {
            EnsureStartedCore(
                config,
                SshTunnelOwner.GatewayConnectionManager,
                RejectOccupiedForwardPorts);

            var normalizedConfig = config with
            {
                User = config.User.Trim(),
                Host = config.Host.Trim(),
            };
            DateTime processStartTimeUtc;
            lock (_stateLock)
            {
                if (!IsRunningLocked() ||
                    _process is null ||
                    !Equals(_currentConfig, normalizedConfig) ||
                    _currentOwner != SshTunnelOwner.GatewayConnectionManager)
                {
                    throw new InvalidOperationException("SSH tunnel changed before listener ownership could be verified.");
                }

                process = _process;
                generation = _lifecycleGeneration;
                processStartTimeUtc = process.StartTime.ToUniversalTime();
            }

            var processId = process.Id;
            await WaitForOwnedLocalListenerAsync(
                config.LocalPort,
                process,
                generation,
                processId,
                processStartTimeUtc,
                ct).ConfigureAwait(false);
            if (config.IncludeBrowserProxyForward)
            {
                await WaitForOwnedLocalListenerAsync(
                    config.LocalPort + 2,
                    process,
                    generation,
                    processId,
                    processStartTimeUtc,
                    ct).ConfigureAwait(false);
            }
            return new SshTunnelStartResult(
                $"ws://localhost:{config.LocalPort}",
                normalizedConfig,
                generation);
        }
        catch
        {
            if (process is not null)
                StopIfCurrent(process, generation);
            throw;
        }
    }

    public Task StopAsync()
    {
        Stop();
        return Task.CompletedTask;
    }

    public Task<bool> StopIfOwnedAsync(
        SshTunnelConfig config,
        long ownershipGeneration,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        while (!Monitor.TryEnter(_operationLock, millisecondsTimeout: 50))
            ct.ThrowIfCancellationRequested();
        try
        {
            ct.ThrowIfCancellationRequested();
            Process? process;
            lock (_stateLock)
            {
                ct.ThrowIfCancellationRequested();
                var normalizedConfig = config with
                {
                    User = config.User.Trim(),
                    Host = config.Host.Trim(),
                };
                if (_lifecycleGeneration != ownershipGeneration ||
                    !Equals(_currentConfig, normalizedConfig) ||
                    _currentOwner != SshTunnelOwner.GatewayConnectionManager ||
                    _browserHandoffLeases > 0)
                {
                    return Task.FromResult(false);
                }

                process = ClaimProcessForStopLocked();
            }

            StopClaimedProcess(process);
            return Task.FromResult(true);
        }
        finally
        {
            Monitor.Exit(_operationLock);
        }
    }

    private async Task WaitForOwnedLocalListenerAsync(
        int localPort,
        Process process,
        long generation,
        int processId,
        DateTime processStartTimeUtc,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(TimeSpan.FromSeconds(20).TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_stateLock)
            {
                if (generation != _lifecycleGeneration ||
                    !ReferenceEquals(_process, process) ||
                    !IsRunningLocked())
                {
                    throw new InvalidOperationException(
                        LastError ?? "SSH tunnel changed before its local listener became ready.");
                }
            }

            if (ValidateListenerOwnership(
                WindowsTcpListenerSnapshot.Capture(),
                localPort,
                processId,
                processStartTimeUtc))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"SSH tunnel did not establish ownership of local listener port {localPort}.");
    }

    private void StopIfCurrent(Process process, long generation)
    {
        lock (_operationLock)
        {
            Process? claimed;
            lock (_stateLock)
            {
                if (generation != _lifecycleGeneration ||
                    !ReferenceEquals(_process, process) ||
                    _browserHandoffLeases > 0)
                {
                    return;
                }

                claimed = ClaimProcessForStopLocked();
            }

            StopClaimedProcess(claimed);
        }
    }

    internal static void EnsurePortIsUnoccupied(
        WindowsTcpListenerSnapshotResult snapshot,
        int localPort)
    {
        EnsureCompleteListenerSnapshot(snapshot);
        if (snapshot.Listeners.Any(listener =>
            listener.Port == localPort && CanServeLoopback(listener.Address)))
            throw new InvalidOperationException($"Local port {localPort} is already owned by another process.");
    }

    internal static bool ValidateListenerOwnership(
        WindowsTcpListenerSnapshotResult snapshot,
        int localPort,
        int processId,
        DateTime processStartTimeUtc)
    {
        EnsureCompleteListenerSnapshot(snapshot);
        var listeners = snapshot.Listeners
            .Where(listener =>
                listener.Port == localPort && CanServeLoopback(listener.Address))
            .ToArray();
        if (listeners.Length == 0)
            return false;

        if (listeners.Any(listener =>
            listener.ProcessId != processId ||
            listener.ProcessStartTimeUtc != processStartTimeUtc))
        {
            throw new InvalidOperationException(
                $"Local port {localPort} is not owned exclusively by the launched SSH process.");
        }

        return true;
    }

    private static bool CanServeLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address) ||
        address.Equals(IPAddress.Any) ||
        address.Equals(IPAddress.IPv6Any);

    private static void EnsureCompleteListenerSnapshot(WindowsTcpListenerSnapshotResult snapshot)
    {
        if (!snapshot.Ipv4Complete || !snapshot.Ipv6Complete)
            throw new InvalidOperationException("TCP listener ownership could not be verified.");
    }
}

public sealed record SshTunnelExit(
    int ExitCode,
    SshTunnelConfig Tunnel,
    long Generation,
    SshTunnelOwner Owner = SshTunnelOwner.Unspecified);

public enum SshTunnelOwner
{
    Unspecified = 0,
    Settings,
    GatewayConnectionManager
}
