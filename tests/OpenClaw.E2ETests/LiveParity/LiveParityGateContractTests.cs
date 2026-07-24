namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Secretless, fixture-free contract tests for the live-parity opt-in
/// gates and their Fact attributes. These never touch WSL/tray/gateway
/// state; they only exercise environment-variable-driven skip logic.
/// Every test that mutates a gate environment variable restores it in
/// <see cref="Dispose"/> so this class can run alongside every other test
/// in the (sequentially executed, see AssemblyInfo.cs) E2E assembly
/// without leaking state to other tests.
/// </summary>
public sealed class LiveParityGateContractTests : IDisposable
{
    private readonly string? _savedRunE2E;
    private readonly string? _savedRunLiveModel;
    private readonly string? _savedRunRealChannel;

    public LiveParityGateContractTests()
    {
        _savedRunE2E = Environment.GetEnvironmentVariable(LiveParityEnvVars.RunE2E);
        _savedRunLiveModel = Environment.GetEnvironmentVariable(LiveParityEnvVars.RunLiveModelE2E);
        _savedRunRealChannel = Environment.GetEnvironmentVariable(LiveParityEnvVars.RunRealChannelE2E);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunE2E, _savedRunE2E);
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunLiveModelE2E, _savedRunLiveModel);
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunRealChannelE2E, _savedRunRealChannel);
    }

    [Fact]
    public void LiveModelGate_WhenBaseE2EDisabled_SkipReasonNamesRunE2EVariable()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunE2E, null);
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunLiveModelE2E, "1");

        var reason = LiveModelE2ETestGate.SkipReason;

        Assert.NotNull(reason);
        Assert.Contains(LiveParityEnvVars.RunE2E, reason);
    }

    [Fact]
    public void LiveModelGate_WhenBaseEnabledButOwnDisabled_SkipReasonNamesOwnVariableOnly()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunE2E, "1");
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunLiveModelE2E, null);

        var reason = LiveModelE2ETestGate.SkipReason;

        Assert.NotNull(reason);
        Assert.Contains(LiveParityEnvVars.RunLiveModelE2E, reason);
        Assert.DoesNotContain(LiveParityEnvVars.RunRealChannelE2E, reason);
    }

    [Fact]
    public void LiveModelGate_WhenBothEnabled_DoesNotSkip_RegardlessOfProfileOrSecret()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunE2E, "1");
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunLiveModelE2E, "true");
        // Deliberately do NOT set a profile path or any credential: the gate
        // itself must proceed (return null) without knowing or caring
        // whether a profile/secret is present. Fail-closed is enforced by
        // the profile loader/credential resolver, not by this gate.
        Environment.SetEnvironmentVariable(LiveParityEnvVars.LiveModelProfilePath, null);

        Assert.Null(LiveModelE2ETestGate.SkipReason);
    }

    [Fact]
    public void RealChannelGate_WhenBaseE2EDisabled_SkipReasonNamesRunE2EVariable()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunE2E, null);
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunRealChannelE2E, "1");

        var reason = RealChannelE2ETestGate.SkipReason;

        Assert.NotNull(reason);
        Assert.Contains(LiveParityEnvVars.RunE2E, reason);
    }

    [Fact]
    public void RealChannelGate_WhenBaseEnabledButOwnDisabled_SkipReasonNamesOwnVariableOnly()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunE2E, "1");
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunRealChannelE2E, null);

        var reason = RealChannelE2ETestGate.SkipReason;

        Assert.NotNull(reason);
        Assert.Contains(LiveParityEnvVars.RunRealChannelE2E, reason);
        Assert.DoesNotContain(LiveParityEnvVars.RunLiveModelE2E, reason);
    }

    [Fact]
    public void RealChannelGate_WhenBothEnabled_DoesNotSkip_RegardlessOfProfileOrSecret()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunE2E, "1");
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunRealChannelE2E, "1");
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RealChannelProfilePath, null);

        Assert.Null(RealChannelE2ETestGate.SkipReason);
    }

    [Fact]
    public void LiveModelFactAttribute_SkipProperty_MirrorsGateSkipReason()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunE2E, null);
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunLiveModelE2E, null);

        var attribute = new LiveModelE2EFactAttribute();

        Assert.Equal(LiveModelE2ETestGate.SkipReason, attribute.Skip);
        Assert.NotNull(attribute.Skip);
    }

    [Fact]
    public void RealChannelFactAttribute_SkipProperty_MirrorsGateSkipReason()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunE2E, null);
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunRealChannelE2E, null);

        var attribute = new RealChannelE2EFactAttribute();

        Assert.Equal(RealChannelE2ETestGate.SkipReason, attribute.Skip);
        Assert.NotNull(attribute.Skip);
    }

    [Fact]
    public void LiveModelFactAttribute_SkipProperty_IsNull_WhenBothEnabled()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunE2E, "1");
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunLiveModelE2E, "1");

        var attribute = new LiveModelE2EFactAttribute();

        Assert.Null(attribute.Skip);
    }

    [Fact]
    public void RealChannelFactAttribute_SkipProperty_IsNull_WhenBothEnabled()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunE2E, "1");
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RunRealChannelE2E, "1");

        var attribute = new RealChannelE2EFactAttribute();

        Assert.Null(attribute.Skip);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("yes", false)]
    [InlineData("enabled", false)]
    public void IsTruthy_AcceptsOnly1OrTrueCaseInsensitive(string? value, bool expected)
    {
        const string tempVar = "OPENCLAW_LIVE_PARITY_CONTRACT_TEST_TRUTHY";
        var saved = Environment.GetEnvironmentVariable(tempVar);
        try
        {
            Environment.SetEnvironmentVariable(tempVar, value);
            Assert.Equal(expected, LiveParityEnvVars.IsTruthy(tempVar));
        }
        finally
        {
            Environment.SetEnvironmentVariable(tempVar, saved);
        }
    }
}
