using System;

namespace OpenClawTray.Chat;

/// <summary>
/// Coalesces immutable chat snapshots to the latest state while one UI delivery
/// is pending. Timeline reduction still processes every gateway event in order;
/// only superseded render notifications are skipped.
/// </summary>
internal sealed class ChatSnapshotDelivery<T>
{
    private readonly object _gate = new();
    private readonly Action<Action>? _post;
    private readonly Action<T> _deliver;
    private Func<T>? _pendingFactory;
    private bool _scheduled;
    private bool _canceled;

    public ChatSnapshotDelivery(Action<Action>? post, Action<T> deliver)
    {
        _post = post;
        _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
    }

    public void Publish(T value)
        => PublishFactory(() => value);

    /// <summary>
    /// Request delivery of state that is materialized only if this request is
    /// still the latest when the dispatcher runs.
    /// </summary>
    public void PublishFactory(Func<T> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        if (_post is null)
        {
            lock (_gate)
            {
                if (_canceled)
                    return;
            }
            _deliver(valueFactory());
            return;
        }

        var shouldSchedule = false;
        lock (_gate)
        {
            if (_canceled)
                return;

            _pendingFactory = valueFactory;
            if (!_scheduled)
            {
                _scheduled = true;
                shouldSchedule = true;
            }
        }

        if (shouldSchedule)
            ScheduleDelivery();
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _canceled = true;
            _pendingFactory = null;
        }
    }

    private void DeliverPending()
    {
        Func<T>? valueFactory;
        lock (_gate)
        {
            if (_canceled)
            {
                _scheduled = false;
                _pendingFactory = null;
                return;
            }

            valueFactory = _pendingFactory;
            _pendingFactory = null;
        }

        try
        {
            if (valueFactory is not null)
                _deliver(valueFactory());
        }
        finally
        {
            var shouldSchedule = false;
            lock (_gate)
            {
                if (_canceled || _pendingFactory is null)
                    _scheduled = false;
                else
                    shouldSchedule = true;
            }

            if (shouldSchedule)
                ScheduleDelivery();
        }
    }

    private void ScheduleDelivery()
    {
        try
        {
            _post!(DeliverPending);
        }
        catch
        {
            lock (_gate)
            {
                _scheduled = false;
                _pendingFactory = null;
            }
            throw;
        }
    }
}
