using OpenClawTray.Chat;
using OpenClawTray.Services;
using OpenClaw.Shared;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

public sealed class StreamingBackpressureTests
{
    [Fact]
    public void GatewayAndProviderDelegateStreamingBackpressureToDedicatedOwners()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var gateway = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "GatewayService.cs"));
        var provider = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs"));

        Assert.Contains("BoundedDispatcherBatchQueue<QueuedAgentEvent>", gateway);
        Assert.Contains("_agentEventQueue.Enqueue", gateway);
        Assert.Contains("ReferenceEquals(queued.Client, _currentClient)", gateway);
        Assert.Contains("OnAgentEventReceived(sender, evt, generation)", gateway);
        Assert.Contains("AgentEventBackpressurePolicy.IsDisposableUpdate", gateway);
        Assert.DoesNotContain("AgentEventReceived += OnAgentEventReceived", gateway);
        Assert.DoesNotContain("EnqueueModelUpdate(() => _state.AddAgentEvent", gateway);
        Assert.Contains("ChatSnapshotDelivery<ChatDataSnapshot>", provider);
        Assert.Contains("_snapshotDelivery.PublishFactory(BuildCurrentSnapshot)", provider);
        Assert.DoesNotContain("_post(() => Changed?.Invoke", provider);
    }

    [Fact]
    public void AgentEventQueue_BurstRetainsNewestBoundedTailAndYieldsBetweenBatches()
    {
        var dispatcher = new Queue<Action>();
        var consumed = new List<int>();
        var queue = new BoundedDispatcherBatchQueue<int>(
            action =>
            {
                dispatcher.Enqueue(action);
                return true;
            },
            consumed.Add,
            maxPending: 400,
            maxBatchSize: 32);

        const int eventCount = 10_000;
        for (var i = 0; i < eventCount; i++)
            queue.Enqueue(i);

        Assert.Single(dispatcher);
        var dispatcherTurns = 0;
        while (dispatcher.TryDequeue(out var work))
        {
            dispatcherTurns++;
            var before = consumed.Count;
            work();
            Assert.InRange(consumed.Count - before, 1, 32);
            Assert.InRange(dispatcher.Count, 0, 1);
        }

        Assert.Equal(400, consumed.Count);
        Assert.Equal(13, dispatcherTurns);
        Assert.Equal(Enumerable.Range(eventCount - 400, 400), consumed);
    }

    [Fact]
    public void AgentEventQueue_ClearCancelsPendingGeneration()
    {
        var dispatcher = new Queue<Action>();
        var consumed = new List<int>();
        var queue = new BoundedDispatcherBatchQueue<int>(
            action =>
            {
                dispatcher.Enqueue(action);
                return true;
            },
            consumed.Add,
            maxPending: 8,
            maxBatchSize: 2);

        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Clear();
        dispatcher.Dequeue()();

        Assert.Empty(consumed);
        queue.Enqueue(3);
        Assert.Single(dispatcher);
        dispatcher.Dequeue()();
        Assert.Equal([3], consumed);
    }

    [Fact]
    public void AgentEventQueue_StreamOverflowPreservesLifecycleControlEvent()
    {
        var dispatcher = new Queue<Action>();
        var consumed = new List<int>();
        var queue = new BoundedDispatcherBatchQueue<int>(
            action =>
            {
                dispatcher.Enqueue(action);
                return true;
            },
            consumed.Add,
            maxPending: 400,
            maxBatchSize: 32,
            preferEviction: value => value >= 0);

        const int lifecycleEnd = -1;
        queue.Enqueue(lifecycleEnd);
        for (var i = 0; i < 10_000; i++)
            queue.Enqueue(i);

        while (dispatcher.TryDequeue(out var work))
            work();

        Assert.Equal(400, consumed.Count);
        Assert.Equal(lifecycleEnd, consumed[0]);
        Assert.Equal(Enumerable.Range(10_000 - 399, 399), consumed.Skip(1));
    }

    [Fact]
    public void AgentEventQueue_ControlOnlyCapacityRejectsIncomingDisposableStream()
    {
        var dispatcher = new Queue<Action>();
        var consumed = new List<int>();
        var queue = new BoundedDispatcherBatchQueue<int>(
            action =>
            {
                dispatcher.Enqueue(action);
                return true;
            },
            consumed.Add,
            maxPending: 4,
            maxBatchSize: 2,
            preferEviction: value => value >= 100);

        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);
        queue.Enqueue(4);
        queue.Enqueue(100);

        while (dispatcher.TryDequeue(out var work))
            work();

        Assert.Equal([1, 2, 3, 4], consumed);
    }

    [Fact]
    public void AgentEventPolicy_DropsOnlyKnownHighVolumeUpdatePhases()
    {
        Assert.True(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("assistant", "{}")));
        Assert.True(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("command_output", "{}")));
        Assert.True(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("item", """{"phase":"update","kind":"command"}""")));
        Assert.True(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("tool", """{"phase":"progress"}""")));
        Assert.True(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("job", """{"state":"running"}""")));

        Assert.False(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("lifecycle", """{"phase":"end"}""")));
        Assert.False(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("item", """{"phase":"start"}""")));
        Assert.False(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("item", """{"phase":"end"}""")));
        Assert.False(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("tool", """{"phase":"start"}""")));
        Assert.False(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("tool", """{"phase":"result"}""")));
        Assert.False(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("tool", """{"phase":"error"}""")));
        Assert.False(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("job", """{"state":"done"}""")));
        Assert.False(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("job", """{"state":"error"}""")));
        Assert.False(AgentEventBackpressurePolicy.IsDisposableUpdate(Event("tool", """{"phase":"future"}""")));
    }

    [Fact]
    public void AgentEventQueue_ConsumerFailureStillSchedulesPendingBatch()
    {
        var dispatcher = new Queue<Action>();
        var consumed = new List<int>();
        var queue = new BoundedDispatcherBatchQueue<int>(
            action =>
            {
                dispatcher.Enqueue(action);
                return true;
            },
            item =>
            {
                if (item == 1)
                    throw new InvalidOperationException("simulated handled listener failure");
                consumed.Add(item);
            },
            maxPending: 4,
            maxBatchSize: 2);

        queue.Enqueue(1);
        queue.Enqueue(2);

        Assert.Throws<InvalidOperationException>(() => dispatcher.Dequeue()());
        Assert.Single(dispatcher);
        dispatcher.Dequeue()();
        Assert.Equal([2], consumed);
    }

    [Fact]
    public void ChatSnapshotDelivery_BurstQueuesOneCallbackAndDeliversLatestState()
    {
        var dispatcher = new Queue<Action>();
        var delivered = new List<int>();
        var delivery = new ChatSnapshotDelivery<int>(dispatcher.Enqueue, delivered.Add);

        const int snapshotCount = 10_000;
        for (var i = 0; i < snapshotCount; i++)
            delivery.Publish(i);

        Assert.Single(dispatcher);
        dispatcher.Dequeue()();
        Assert.Equal([snapshotCount - 1], delivered);

        delivery.Publish(snapshotCount);
        Assert.Single(dispatcher);
        dispatcher.Dequeue()();
        Assert.Equal([snapshotCount - 1, snapshotCount], delivered);
    }

    [Fact]
    public void ChatSnapshotDelivery_CancelDropsQueuedRenderWork()
    {
        var dispatcher = new Queue<Action>();
        var delivered = new List<int>();
        var delivery = new ChatSnapshotDelivery<int>(dispatcher.Enqueue, delivered.Add);

        delivery.Publish(1);
        delivery.Cancel();
        dispatcher.Dequeue()();
        delivery.Publish(2);

        Assert.Empty(delivered);
        Assert.Empty(dispatcher);
    }

    [Fact]
    public void ChatSnapshotDelivery_PublishDuringDeliveryQueuesLatestAfterCurrent()
    {
        var dispatcher = new Queue<Action>();
        var delivered = new List<int>();
        ChatSnapshotDelivery<int>? delivery = null;
        delivery = new ChatSnapshotDelivery<int>(dispatcher.Enqueue, value =>
        {
            delivered.Add(value);
            if (value == 1)
                delivery!.Publish(2);
        });

        delivery.Publish(1);
        dispatcher.Dequeue()();
        Assert.Equal([1], delivered);
        Assert.Single(dispatcher);

        dispatcher.Dequeue()();
        Assert.Equal([1, 2], delivered);
    }

    private static AgentEventInfo Event(string stream, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new AgentEventInfo
        {
            Stream = stream,
            Data = document.RootElement.Clone(),
        };
    }
}
