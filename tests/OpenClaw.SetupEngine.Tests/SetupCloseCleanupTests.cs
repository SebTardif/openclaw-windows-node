namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupCloseCleanupTests : IDisposable
{
    private readonly string _tempDir;

    public SetupCloseCleanupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"setup-close-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Close_ReleasesSetupLockOnlyAfterProgressRollbackFinishes()
    {
        Assert.True(SetupRunLock.TryAcquire(_tempDir, out var held, out var message));
        Assert.Null(message);

        var rollback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = Task.Run(async () =>
        {
            try
            {
                await SetupCloseCleanup.WaitForRunningWorkAsync(Task.CompletedTask, rollback.Task);
            }
            finally
            {
                held!.Dispose();
            }
        });

        var finishedEarly = await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(cleanup, finishedEarly);
        Assert.False(SetupRunLock.TryAcquire(_tempDir, out var stolen, out _));
        stolen?.Dispose();

        rollback.SetResult();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(SetupRunLock.TryAcquire(_tempDir, out var next, out var nextMessage));
        Assert.Null(nextMessage);
        next?.Dispose();
    }

    [Fact]
    public async Task WaitForRunningWork_StillWaitsForPipelineWhenContextFaults()
    {
        var rollback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = Task.FromException(new InvalidOperationException("context failed"));
        var waiting = SetupCloseCleanup.WaitForRunningWorkAsync(context, rollback.Task);

        var finishedEarly = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(waiting, finishedEarly);

        rollback.SetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => waiting);
    }

    [Fact]
    public async Task FaultedContext_RunsTeardownAfterPipelineAndBeforeLockRelease()
    {
        Assert.True(SetupRunLock.TryAcquire(_tempDir, out var held, out var message));
        Assert.Null(message);

        var pipeline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = Task.FromException(new InvalidOperationException("context failed"));
        var teardownStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = SetupCloseCleanup.WaitInsideTeardownAsync(
            context,
            pipeline.Task,
            () =>
            {
                teardownStarted.TrySetResult();
                return Task.CompletedTask;
            });

        var finishedEarly = await Task.WhenAny(teardownStarted.Task, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(teardownStarted.Task, finishedEarly);
        Assert.False(SetupRunLock.TryAcquire(_tempDir, out var stolen, out _));
        stolen?.Dispose();

        pipeline.SetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => waiting);
        Assert.True(teardownStarted.Task.IsCompletedSuccessfully);
        Assert.False(SetupRunLock.TryAcquire(_tempDir, out stolen, out _));
        stolen?.Dispose();

        held!.Dispose();
        Assert.True(SetupRunLock.TryAcquire(_tempDir, out var next, out var nextMessage));
        Assert.Null(nextMessage);
        next?.Dispose();
    }
}
