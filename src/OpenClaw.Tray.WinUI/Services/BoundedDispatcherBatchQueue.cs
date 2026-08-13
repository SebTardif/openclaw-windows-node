using System;
using System.Collections.Generic;

namespace OpenClawTray.Services;

/// <summary>
/// Bounds producer pressure on a single-threaded dispatcher. Pending items retain
/// arrival order, with a configurable preferred-eviction policy when the producer
/// outruns the UI. Each dispatcher turn consumes a configured maximum batch so
/// input and lifecycle work can run too.
/// </summary>
internal sealed class BoundedDispatcherBatchQueue<T>
{
    private readonly object _gate = new();
    private readonly LinkedList<T> _pending = new();
    private readonly Func<Action, bool> _schedule;
    private readonly Action<T> _consume;
    private readonly int _maxPending;
    private readonly int _maxBatchSize;
    private readonly Func<T, bool>? _preferEviction;
    private bool _scheduled;

    public BoundedDispatcherBatchQueue(
        Func<Action, bool> schedule,
        Action<T> consume,
        int maxPending,
        int maxBatchSize,
        Func<T, bool>? preferEviction = null)
    {
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _consume = consume ?? throw new ArgumentNullException(nameof(consume));
        if (maxPending <= 0) throw new ArgumentOutOfRangeException(nameof(maxPending));
        if (maxBatchSize <= 0 || maxBatchSize > maxPending)
            throw new ArgumentOutOfRangeException(nameof(maxBatchSize));

        _maxPending = maxPending;
        _maxBatchSize = maxBatchSize;
        _preferEviction = preferEviction;
    }

    public void Enqueue(T item)
    {
        var shouldSchedule = false;
        lock (_gate)
        {
            if (_pending.Count == _maxPending && !EvictOneOrRejectIncoming(item))
                return;
            _pending.AddLast(item);

            if (!_scheduled)
            {
                _scheduled = true;
                shouldSchedule = true;
            }
        }

        if (shouldSchedule)
            ScheduleDrain();
    }

    /// <summary>Discard pending work, for example when its gateway generation is replaced.</summary>
    public void Clear()
    {
        lock (_gate)
            _pending.Clear();
    }

    private void Drain()
    {
        List<T> batch;
        lock (_gate)
        {
            var count = Math.Min(_pending.Count, _maxBatchSize);
            batch = new List<T>(count);
            for (var i = 0; i < count; i++)
            {
                batch.Add(_pending.First!.Value);
                _pending.RemoveFirst();
            }
        }

        try
        {
            for (var i = 0; i < batch.Count; i++)
            {
                try
                {
                    _consume(batch[i]);
                }
                catch
                {
                    lock (_gate)
                    {
                        for (var suffix = batch.Count - 1; suffix > i; suffix--)
                            _pending.AddFirst(batch[suffix]);
                        while (_pending.Count > _maxPending)
                            EvictPendingPreferredOrOldest();
                    }
                    throw;
                }
            }
        }
        finally
        {
            var shouldSchedule = false;
            lock (_gate)
            {
                if (_pending.Count == 0)
                    _scheduled = false;
                else
                    shouldSchedule = true;
            }

            if (shouldSchedule)
                ScheduleDrain();
        }
    }

    private bool EvictOneOrRejectIncoming(T incoming)
    {
        if (TryEvictPreferredPending())
            return true;

        if (_preferEviction is not null && _preferEviction(incoming))
            return false;

        _pending.RemoveFirst();
        return true;
    }

    private void EvictPendingPreferredOrOldest()
    {
        if (!TryEvictPreferredPending())
            _pending.RemoveFirst();
    }

    private bool TryEvictPreferredPending()
    {
        if (_preferEviction is not null)
        {
            for (var node = _pending.First; node is not null; node = node.Next)
            {
                if (_preferEviction(node.Value))
                {
                    _pending.Remove(node);
                    return true;
                }
            }
        }

        return false;
    }

    private void ScheduleDrain()
    {
        if (_schedule(Drain))
            return;

        lock (_gate)
        {
            _scheduled = false;
            _pending.Clear();
        }
    }
}
