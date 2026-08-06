using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public class WizardTimeoutsTests
{
    [Fact]
    public void RestartRecovery_PreservesTwoReplayLimit()
    {
        Assert.Equal(2, SetupWizardRunner.MaxWizardRestartAttempts);
    }

    [Theory]
    [InlineData("Gateway connection lost while waiting for wizard response")]
    [InlineData("gateway restarting after config change")]
    [InlineData("service restart interrupted the request")]
    public void RestartRecovery_ClassifiesOnlyIntentionalRestartSignals(string message)
    {
        Assert.True(SetupWizardRunner.ShouldRecoverAfterWizardRequestFailure(
            new InvalidOperationException(message),
            cancellationRequested: false,
            restartAttempts: 0));
    }

    [Theory]
    [InlineData("No connection could be made because the target machine actively refused it")]
    [InlineData("wizard response was malformed")]
    [InlineData("operator pairing required")]
    public void RestartRecovery_DoesNotClassifyArbitraryFailures(string message)
    {
        Assert.False(SetupWizardRunner.ShouldRecoverAfterWizardRequestFailure(
            new InvalidOperationException(message),
            cancellationRequested: false,
            restartAttempts: 0));
    }

    [Fact]
    public void RestartRecovery_DoesNotRecoverAfterCancellationOrReplayLimit()
    {
        var restart = new InvalidOperationException(
            "Gateway connection lost while waiting for wizard response");

        Assert.False(SetupWizardRunner.ShouldRecoverAfterWizardRequestFailure(
            restart,
            cancellationRequested: true,
            restartAttempts: 0));
        Assert.False(SetupWizardRunner.ShouldRecoverAfterWizardRequestFailure(
            restart,
            cancellationRequested: false,
            restartAttempts: SetupWizardRunner.MaxWizardRestartAttempts));
    }

    [Fact]
    public void RestartRecovery_SourceContract_StartsServiceOnceAndDoesNotResendAnswer()
    {
        var sourcePath = Path.Combine(
            RepositoryRoot(),
            "src",
            "OpenClaw.SetupEngine",
            "SetupWizardRunner.cs");
        var source = File.ReadAllText(sourcePath);
        const string catchMarker =
            "catch (Exception ex) when (ShouldRecoverAfterWizardRequestFailure(";
        var start = source.IndexOf(catchMarker, StringComparison.Ordinal);
        var end = source.IndexOf(
            "\n            while (true)",
            start,
            StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "Classified restart catch was not found.");
        var recoveryBlock = source[start..end];
        Assert.Equal(1, CountOccurrences(
            recoveryBlock,
            "WakeGatewayServiceAfterWizardRestartAsync("));
        Assert.Equal(1, CountOccurrences(
            recoveryBlock,
            "SendWizardRequestAsync(\"wizard.start\""));
        Assert.DoesNotContain(
            "SendWizardRequestAsync(\"wizard.next\"",
            recoveryBlock,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestartServiceRecovery_StartsExactUnitOnceWithBoundedTimeout()
    {
        var commands = new RestartCommandRunner(
            new CommandResult(0, "", "", TimeSpan.FromMilliseconds(20), TimedOut: false));

        var result = await SetupWizardRunner.WakeGatewayServiceAfterWizardRestartAsync(
            commands,
            "OpenClawE2E-test",
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        var call = Assert.Single(commands.WslCalls);
        Assert.Equal("OpenClawE2E-test", call.DistroName);
        Assert.Equal(SetupWizardRunner.RestartGatewayServiceCommand, call.Command);
        Assert.Equal(SetupWizardRunner.RestartGatewayServiceTimeout, call.Timeout);
    }

    [Fact]
    public async Task RestartServiceRecovery_TimeoutFailsClosedWithBoundedSanitizedDiagnostic()
    {
        var secret = new string('a', 64);
        var commands = new RestartCommandRunner(
            new CommandResult(
                -1,
                "",
                $"token={secret} {string.Join(' ', Enumerable.Repeat("failure", 800))}",
                SetupWizardRunner.RestartGatewayServiceTimeout,
                TimedOut: true));

        var result = await SetupWizardRunner.WakeGatewayServiceAfterWizardRestartAsync(
            commands,
            "OpenClawE2E-test",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("timed out after 30 seconds", result.Message);
        Assert.Contains("[truncated]", result.Message);
        Assert.DoesNotContain(secret, result.Message);
        Assert.True(result.Message!.Length < 2300);
    }

    [Fact]
    public async Task RestartServiceRecovery_NonzeroFailsClosedWithoutReconnect()
    {
        var commands = new RestartCommandRunner(
            new CommandResult(
                1,
                "",
                "Failed to start exact unit",
                TimeSpan.FromMilliseconds(20),
                TimedOut: false));

        var result = await SetupWizardRunner.WakeGatewayServiceAfterWizardRestartAsync(
            commands,
            "OpenClawE2E-test",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("failed with exit 1", result.Message);
        Assert.Contains("Failed to start exact unit", result.Message);
        Assert.Single(commands.WslCalls);
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

    private sealed class RestartCommandRunner(CommandResult result) : ICommandRunner
    {
        public List<(string DistroName, string Command, TimeSpan Timeout)> WslCalls { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Windows process execution is not expected.");

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false)
        {
            ct.ThrowIfCancellationRequested();
            WslCalls.Add((distroName, command, timeout));
            return Task.FromResult(result);
        }
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string RepositoryRoot()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") is { Length: > 0 } configured)
            return configured;

        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "openclaw-windows-node.slnx")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
