using System.Collections.Immutable;
using OpenClaw.Chat;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatHistoryMergeTests
{
    [Fact]
    public void Merge_KeepsEchoedDuplicateMissingFromTheSnapshot()
    {
        var when = DateTimeOffset.UnixEpoch.AddHours(1);
        var history = User("e1", "ok");
        var plan = new ChatHistoryRebuildPlan(
            "session",
            Timeline(history),
            Meta(("e1", when, "msg-1", 1)),
            MaxHistorySequence: 1);
        var live = User("live-2", "ok");
        var prior = Timeline(live);
        var priorMeta = Meta(("live-2", when.AddSeconds(1), "msg-2", 2));

        var merged = ChatHistoryState.MergeWithLiveEntries(
            plan,
            prior,
            priorMeta,
            when,
            authoritative: false);

        Assert.Equal(2, merged.Timeline.Entries.Count);
        Assert.Contains(
            merged.Metadata.Values,
            metadata => metadata.GatewayMessageId == "msg-2");
    }

    [Fact]
    public void Merge_DropsOptimisticDuplicateWithinTwoSeconds()
    {
        var when = DateTimeOffset.UnixEpoch.AddHours(1);
        var history = User("e1", "ok");
        var plan = new ChatHistoryRebuildPlan(
            "session",
            Timeline(history),
            Meta(("e1", when, "msg-1", 1)),
            MaxHistorySequence: 1);
        var live = User("live-1", "ok");
        var prior = Timeline(live);
        var priorMeta = new Dictionary<string, ChatEntryMetadata>
        {
            ["live-1"] = new(when.AddSeconds(1), Model: null),
        };

        var merged = ChatHistoryState.MergeWithLiveEntries(
            plan,
            prior,
            priorMeta,
            when,
            authoritative: false);

        Assert.Single(merged.Timeline.Entries);
        Assert.Equal("e1", merged.Timeline.Entries[0].Id);
    }

    private static ChatTimelineItem User(string id, string text) =>
        new(id, ChatTimelineItemKind.User, text);

    private static ChatTimelineState Timeline(params ChatTimelineItem[] items) =>
        ChatTimelineState.Initial() with
        {
            Entries = items.ToImmutableList(),
            NextId = items.Length + 1,
            HistoryLoaded = true,
        };

    private static Dictionary<string, ChatEntryMetadata> Meta(
        params (string Id, DateTimeOffset Timestamp, string GatewayId, int Sequence)[] rows)
    {
        var metadata = new Dictionary<string, ChatEntryMetadata>();
        foreach (var row in rows)
        {
            metadata[row.Id] = new(
                row.Timestamp,
                Model: null,
                GatewayMessageId: row.GatewayId,
                OpenClawSeq: row.Sequence);
        }
        return metadata;
    }
}
