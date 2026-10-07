namespace OpenClaw.SetupEngine;

internal static class SetupCloseCleanup
{
    public static async Task WaitForRunningWorkAsync(Task? contextApplyTask, Task? progressPipelineTask)
    {
        if (contextApplyTask is null && progressPipelineTask is null)
            return;

        if (contextApplyTask is null)
        {
            await progressPipelineTask!.ConfigureAwait(false);
            return;
        }

        if (progressPipelineTask is null)
        {
            await contextApplyTask.ConfigureAwait(false);
            return;
        }

        await Task.WhenAll(contextApplyTask, progressPipelineTask).ConfigureAwait(false);
    }

    // The callback resumes on the caller context so window teardown can touch UI.
    public static async Task WaitInsideTeardownAsync(
        Task? contextApplyTask,
        Task? progressPipelineTask,
        Func<Task> teardown)
    {
        ArgumentNullException.ThrowIfNull(teardown);
        try
        {
            await WaitForRunningWorkAsync(contextApplyTask, progressPipelineTask);
        }
        finally
        {
            await teardown();
        }
    }
}
