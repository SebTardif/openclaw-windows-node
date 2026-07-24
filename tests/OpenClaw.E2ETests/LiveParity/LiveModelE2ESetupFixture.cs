using OpenClaw.E2ETests;
using OpenClaw.E2ETests.Setup;
using Xunit;

namespace OpenClaw.E2ETests.LiveParity;

// AssemblyInfo disables parallelization for the whole E2E assembly; this separate
// collection exists so the live model gate can skip before initializing WSL/tray
// state, and so this lane's gateway config mutations never touch the shared
// "E2E Setup" collection's instance used by other, non-secret-gated E2E tests.
[CollectionDefinition("E2E Live Model Setup", DisableParallelization = true)]
public sealed class LiveModelE2ESetupCollection : ICollectionFixture<LiveModelE2ESetupFixture> { }

/// <summary>
/// Gates the expensive setup fixture before xUnit initializes WSL/tray state for
/// the live model-only proof. Method-level skips are too late for collection
/// fixtures: without this wrapper, a disabled gate would still pay for a full
/// WSL distro + tray spin-up before the (skipped) test ever ran.
/// </summary>
public sealed class LiveModelE2ESetupFixture : IAsyncLifetime
{
    private E2ESetupFixture? _inner;

    public E2ESetupFixture Inner => _inner
        ?? throw new InvalidOperationException($"Live model E2E fixture was not initialized: {LiveModelE2ETestGate.SkipReason ?? "unknown reason"}");

    public async Task InitializeAsync()
    {
        if (LiveModelE2ETestGate.SkipReason is not null)
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
