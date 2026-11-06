using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Audio;

internal static class SingleFlightDownload
{
    private static readonly ConditionalWeakTable<Lazy<Task>, SharedFlight> Flights = new();

    public static async Task RunAsync(
        ConcurrentDictionary<string, Lazy<Task>> inFlight,
        string key,
        Func<CancellationToken, Task> startDownload,
        CancellationToken waitCancellationToken = default,
        TimeSpan? operationTimeout = null)
    {
        var flight = new SharedFlight(operationTimeout);
        var candidate = new Lazy<Task>(() =>
        {
            try
            {
                return startDownload(flight.Token)
                    ?? Task.FromException(new InvalidOperationException("Download factory returned null."));
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        Flights.Add(candidate, flight);
        var lazy = inFlight.GetOrAdd(key, candidate);
        var ownsFlight = ReferenceEquals(lazy, candidate);
        if (!ownsFlight)
        {
            Flights.Remove(candidate);
            flight.Dispose();
        }

        Flights.TryGetValue(lazy, out var active);

        CancellationTokenRegistration registration = default;
        if (active is not null)
        {
            Interlocked.Increment(ref active.Waiters);
            if (waitCancellationToken.CanBeCanceled)
            {
                registration = waitCancellationToken.UnsafeRegister(static state =>
                {
                    var shared = (SharedFlight)state!;
                    if (Interlocked.Decrement(ref shared.Waiters) == 0)
                        shared.Cancel();
                }, active);
            }
        }

        Task task;
        try
        {
            task = lazy.Value;
        }
        catch
        {
            registration.Dispose();
            inFlight.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, lazy));
            throw;
        }

        _ = task.ContinueWith(
            _ =>
            {
                registration.Dispose();
                inFlight.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, lazy));
                if (ownsFlight && active is not null)
                {
                    Flights.Remove(lazy);
                    active.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        if (waitCancellationToken.CanBeCanceled)
            await task.WaitAsync(waitCancellationToken).ConfigureAwait(false);
        else
            await task.ConfigureAwait(false);
    }

    private sealed class SharedFlight : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        public int Waiters;
        public CancellationToken Token => _cts.Token;

        public SharedFlight(TimeSpan? operationTimeout)
        {
            if (operationTimeout is { } timeout && timeout > TimeSpan.Zero)
                _cts.CancelAfter(timeout);
        }

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _cts.Dispose();
    }
}
