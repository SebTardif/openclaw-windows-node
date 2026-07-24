using OpenClaw.E2ETests;
using OpenClaw.E2ETests.Setup;
using Xunit;

namespace OpenClaw.E2ETests.LiveParity;

// AssemblyInfo disables parallelization for the whole E2E assembly; this separate
// collection exists so the real channel gate can skip before initializing WSL/tray
// state, and so this lane's gateway config mutations never touch the shared
// "E2E Setup" collection's instance used by other, non-secret-gated E2E tests.
[CollectionDefinition("E2E Real Channel Setup", DisableParallelization = true)]
public sealed class RealChannelE2ESetupCollection : ICollectionFixture<RealChannelE2ESetupFixture> { }

/// <summary>
/// Gates the expensive setup fixture before xUnit initializes WSL/tray state for
/// the real Discord channel-only proof. Method-level skips are too late for
/// collection fixtures: without this wrapper, a disabled gate would still pay for
/// a full WSL distro + tray spin-up before the (skipped) test ever ran.
/// </summary>
public sealed class RealChannelE2ESetupFixture : IAsyncLifetime
{
    private E2ESetupFixture? _inner;

    public E2ESetupFixture Inner => _inner
        ?? throw new InvalidOperationException($"Real channel E2E fixture was not initialized: {RealChannelE2ETestGate.SkipReason ?? "unknown reason"}");

    public async Task InitializeAsync()
    {
        if (RealChannelE2ETestGate.SkipReason is not null)
            return;

        _inner = new E2ESetupFixture(settingsPatch: null, captureRuntimeArtifacts: false);
        await _inner.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_inner is not null)
            await _inner.DisposeAsync();
    }
}
