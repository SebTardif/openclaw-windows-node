using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public class WizardTimeoutsTests
{
    [Fact]
    public void RestartRecovery_PreservesTwoReplayLimit()
    {
        Assert.Equal(2, SetupWizardRunner.MaxWizardRestartAttempts);
    }

    [Fact]
    public async Task RestartReconnect_RetriesFreshClientsUntilConnected()
    {
        var clock = new FakeWizardReconnectClock();
        var outcomes = new Queue<PairOperatorStep.ConnectionOutcome>(
        [
            PairOperatorStep.ConnectionOutcome.Error,
            PairOperatorStep.ConnectionOutcome.Timeout,
            PairOperatorStep.ConnectionOutcome.Connected
        ]);
        var created = new List<string>();
        var discarded = new List<string>();

        var result = await SetupWizardRunner.WaitForGatewayRestartAsync(
            () =>
            {
                var client = $"client-{created.Count + 1}";
                created.Add(client);
                return client;
            },
            (_, _) => Task.FromResult(outcomes.Dequeue()),
            client =>
            {
                discarded.Add(client);
                return Task.CompletedTask;
            },
            clock,
            CancellationToken.None);

        Assert.True(result.Connected);
        Assert.Equal("client-3", result.Resource);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(["client-1", "client-2", "client-3"], created);
        Assert.Equal(["client-1", "client-2"], discarded);
        Assert.Equal(
            [SetupWizardRunner.RestartReconnectPollDelay,
             SetupWizardRunner.RestartReconnectPollDelay,
             SetupWizardRunner.RestartReconnectPollDelay],
            clock.Delays);
    }

    [Fact]
    public async Task RestartReconnect_UnexpectedPairingFailsClosedWithoutRetry()
    {
        var clock = new FakeWizardReconnectClock();
        var discarded = new List<string>();

        var result = await SetupWizardRunner.WaitForGatewayRestartAsync(
            () => "pairing-client",
            (_, _) => Task.FromResult(PairOperatorStep.ConnectionOutcome.PairingRequired),
            client =>
            {
                discarded.Add(client);
                return Task.CompletedTask;
            },
            clock,
            CancellationToken.None);

        Assert.False(result.Connected);
        Assert.Null(result.Resource);
        Assert.Equal(PairOperatorStep.ConnectionOutcome.PairingRequired, result.LastOutcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(["pairing-client"], discarded);
    }

    [Fact]
    public async Task RestartReconnect_StopsAtAbsoluteDeadline()
    {
        var clock = new FakeWizardReconnectClock();
        var discarded = new List<string>();

        var result = await SetupWizardRunner.WaitForGatewayRestartAsync(
            () => $"client-{discarded.Count + 1}",
            (_, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(31));
                return Task.FromResult(PairOperatorStep.ConnectionOutcome.Error);
            },
            client =>
            {
                discarded.Add(client);
                return Task.CompletedTask;
            },
            clock,
            CancellationToken.None);

        Assert.False(result.Connected);
        Assert.Equal(4, result.Attempts);
        Assert.Equal(4, discarded.Count);
        Assert.True(clock.UtcNow - clock.Start < TimeSpan.FromMinutes(2.5));
    }

    [Fact]
    public async Task RestartReconnect_StopsAtAttemptLimit()
    {
        var clock = new FakeWizardReconnectClock();
        var attempts = 0;

        var result = await SetupWizardRunner.WaitForGatewayRestartAsync(
            () => $"client-{++attempts}",
            (_, _) => Task.FromResult(PairOperatorStep.ConnectionOutcome.Error),
            _ => Task.CompletedTask,
            clock,
            CancellationToken.None);

        Assert.False(result.Connected);
        Assert.Equal(SetupWizardRunner.MaxRestartReconnectAttempts, result.Attempts);
        Assert.Equal(SetupWizardRunner.MaxRestartReconnectAttempts, attempts);
    }

    [Fact]
    public async Task RestartReconnect_CancelsAndDiscardsHungConnectAttempt()
    {
        var clock = new FakeWizardReconnectClock();
        var created = 0;
        var discarded = 0;

        var result = await SetupWizardRunner.WaitForGatewayRestartAsync(
            () => $"client-{++created}",
            async (_, attemptCt) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, attemptCt);
                return PairOperatorStep.ConnectionOutcome.Connected;
            },
            _ =>
            {
                discarded++;
                return Task.CompletedTask;
            },
            clock,
            CancellationToken.None,
            attemptTimeout: TimeSpan.FromMilliseconds(10));

        Assert.False(result.Connected);
        Assert.Equal(PairOperatorStep.ConnectionOutcome.Timeout, result.LastOutcome);
        Assert.Equal(SetupWizardRunner.MaxRestartReconnectAttempts, created);
        Assert.Equal(created, discarded);
    }

    [Fact]
    public async Task RestartReconnect_HonorsCancellationBeforeFirstAttempt()
    {
        var clock = new FakeWizardReconnectClock();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SetupWizardRunner.WaitForGatewayRestartAsync(
                () => "unused",
                (_, _) => Task.FromResult(PairOperatorStep.ConnectionOutcome.Connected),
                _ => Task.CompletedTask,
                clock,
                cancellation.Token));
    }

    [Theory]
    [InlineData("Authorize device")]
    [InlineData("Please sign in to continue")]
    [InlineData("Complete the OAuth login")]
    [InlineData("Open your browser to authenticate")]
    [InlineData("Enter the verification code")]
    public void AuthSteps_GetExtendedTimeout(string text)
    {
        Assert.Equal(WizardTimeouts.SlowStepTimeoutMs, WizardTimeouts.ForStep(text, string.Empty));
    }

    [Fact]
    public void AuthHint_DetectedInMessage()
    {
        Assert.Equal(
            WizardTimeouts.SlowStepTimeoutMs,
            WizardTimeouts.ForStep("Setup", "Visit the device authorization page"));
    }

    [Theory]
    [InlineData("Setup", "Downloading plugin package", "")]
    [InlineData("Setup", "Installing integration", "")]
    [InlineData("Setup", "Working", "install-channel-plugin")]
    public void SlowSetupSteps_GetExtendedTimeout(string title, string message, string stepId)
    {
        Assert.Equal(
            WizardTimeouts.SlowStepTimeoutMs,
            WizardTimeouts.ForStep(title, message, stepId));
    }

    [Theory]
    [InlineData("opaque-value", "Microsoft Teams", "")]
    [InlineData("teams", "Collaboration", "")]
    [InlineData("opaque-value", "Collaboration", "Download and configure the plugin")]
    public void SelectedSlowOptionMetadata_GetsExtendedTimeout(string value, string label, string hint)
    {
        var selected = new WizardOptionValue(
            value,
            label,
            hint,
            JsonSerializer.SerializeToElement(value));

        Assert.Equal(
            WizardTimeouts.SlowStepTimeoutMs,
            WizardTimeouts.ForStep("Choose an integration", "Pick one.", selectedOptions: [selected]));
    }

    [Theory]
    [InlineData("Choose a connector")]
    [InlineData("Enter a friendly name")]
    [InlineData("")]
    public void OrdinarySteps_GetDefaultTimeout(string text)
    {
        Assert.Equal(WizardTimeouts.DefaultTimeoutMs, WizardTimeouts.ForStep(text, string.Empty));
    }

    [Fact]
    public void OrdinarySelectedOption_KeepsDefaultTimeout()
    {
        var selected = new WizardOptionValue(
            "matrix",
            "Matrix",
            "Configure an existing connection",
            JsonSerializer.SerializeToElement("matrix"));

        Assert.Equal(
            WizardTimeouts.DefaultTimeoutMs,
            WizardTimeouts.ForStep("Choose an integration", "Pick one.", selectedOptions: [selected]));
    }

    [Theory]
    [InlineData("__skip__", "Skip for now", "")]
    [InlineData("matrix", "Matrix", "Existing connection")]
    [InlineData("browser", "Open in browser", "")]
    public void ChannelSelector_NonSlowOption_KeepsDefaultTimeout(string value, string label, string hint)
    {
        var selected = new WizardOptionValue(
            value,
            label,
            hint,
            JsonSerializer.SerializeToElement(value));

        Assert.Equal(
            WizardTimeouts.DefaultTimeoutMs,
            WizardTimeouts.ForStep(
                "Choose a channel",
                "Select where OpenClaw should send messages.",
                "select-channel-quickstart",
                [selected]));
    }

    [Fact]
    public void ProgressStep_WithIncidentalOptions_UsesStepMetadata()
    {
        var incidentalOption = new WizardOptionValue(
            "details",
            "Show details",
            "",
            JsonSerializer.SerializeToElement("details"));

        Assert.Equal(
            WizardTimeouts.SlowStepTimeoutMs,
            WizardTimeouts.ForGatewayStep(
                "Setup",
                "Working",
                "install-channel-plugin",
                "progress",
                [incidentalOption]));
    }

    [Fact]
    public void ProgressPollBudget_AllowsSingleLongSetupStepToUseTotalBudget()
    {
        Assert.Equal(WizardTimeouts.MaxTotalProgressPolls, WizardTimeouts.MaxProgressPollsPerStep);
        var totalBudget = TimeSpan.FromTicks(
            WizardTimeouts.ProgressPollDelay.Ticks * WizardTimeouts.MaxTotalProgressPolls);
        Assert.True(
            totalBudget >= TimeSpan.FromMinutes(20));
    }

    private sealed class FakeWizardReconnectClock : SetupWizardRunner.IWizardReconnectClock
    {
        public DateTimeOffset Start { get; } = DateTimeOffset.Parse("2026-08-04T00:00:00Z");
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-08-04T00:00:00Z");
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            UtcNow += delay;
            return Task.CompletedTask;
        }

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }
}
