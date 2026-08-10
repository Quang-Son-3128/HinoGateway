using HinoGateway.Core;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// Every consumer of the tag stream must get the whole stream. With one shared queue, the UI and an
/// upstream publisher would each receive only the batches the other did not grab first — the kind of
/// bug that looks like "the utility link randomly misses values".
/// </summary>
public class TagFanOutTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TagRead Read(string id, double value, int second = 0) =>
        new(id, TagValue.Good(value, T0.AddSeconds(second)));

    [Fact]
    public void Every_subscriber_receives_every_batch()
    {
        var store = new TagStore();
        using var ui = store.Subscribe("ui");
        using var uplink = store.Subscribe("uplink");

        store.Apply([Read("a", 1)]);
        store.Apply([Read("b", 2)]);

        Assert.Equal(2, Drain(ui).Count);
        Assert.Equal(2, Drain(uplink).Count);
    }

    [Fact]
    public void A_slow_subscriber_does_not_starve_the_others()
    {
        var store = new TagStore();
        using var slow = store.Subscribe("slow", capacity: 8);
        using var fast = store.Subscribe("fast", capacity: 64);

        for (var i = 0; i < 40; i++)
            store.Apply([Read("a", i, i)]);

        // The fast consumer sees everything; the slow one is capped but still has the newest values.
        Assert.Equal(40, Drain(fast).Count);
        var slowBatches = Drain(slow);
        Assert.Equal(8, slowBatches.Count);
        Assert.True(slow.Dropped > 0, "a queue that overflowed must report it");
    }

    [Fact]
    public void Keep_newest_subscribers_end_up_with_the_latest_values()
    {
        var store = new TagStore();
        using var subscription = store.Subscribe("ui", capacity: 4, dropOldest: true);

        for (var i = 1; i <= 20; i++)
            store.Apply([Read("a", i, i)]);

        var batches = Drain(subscription);
        Assert.Equal(4, batches.Count);
        // The last batch queued is the last one produced — freshness is what a screen needs.
        Assert.Equal(20.0, batches[^1].Single().Value.Value);
        Assert.Equal(16, subscription.Dropped);
    }

    [Fact]
    public void Keep_oldest_subscribers_hold_on_to_what_they_accepted()
    {
        var store = new TagStore();
        using var subscription = store.Subscribe("uplink", capacity: 4, dropOldest: false);

        for (var i = 1; i <= 20; i++)
            store.Apply([Read("a", i, i)]);

        var batches = Drain(subscription);
        Assert.Equal(4, batches.Count);
        Assert.Equal(1.0, batches[0].Single().Value.Value);
        Assert.Equal(4.0, batches[^1].Single().Value.Value);
        Assert.Equal(16, subscription.Dropped);
    }

    [Fact]
    public void Drops_are_actually_counted()
    {
        // Regression guard: a bounded channel's own Drop* modes report success from TryWrite even
        // when they discarded a batch, which would leave this counter reading zero forever.
        var store = new TagStore();
        using var subscription = store.Subscribe("tiny", capacity: 8);

        for (var i = 0; i < 30; i++)
            store.Apply([Read("a", i, i)]);

        Assert.Equal(22, subscription.Dropped);
        Assert.Contains(store.SubscriptionStats, s => s.Name == "tiny" && s.Dropped == 22);
    }

    [Fact]
    public void A_disposed_subscription_stops_receiving_and_disappears_from_the_stats()
    {
        var store = new TagStore();
        var subscription = store.Subscribe("temporary");

        store.Apply([Read("a", 1)]);
        subscription.Dispose();
        store.Apply([Read("a", 2)]);

        Assert.Single(Drain(subscription));           // only what arrived before disposal
        Assert.Empty(store.SubscriptionStats);
        subscription.Dispose();                        // idempotent
    }

    [Fact]
    public void Batches_are_enriched_before_fan_out_so_every_consumer_sees_the_same_values()
    {
        var store = new TagStore();
        using var a = store.Subscribe("a");
        using var b = store.Subscribe("b");

        store.Apply([Read("t", 42, 0)]);
        store.Apply([Read("t", 42, 5)]);   // same value, later sample

        var fromA = Drain(a);
        var fromB = Drain(b);

        // LastChanged is maintained centrally: both consumers see the first change instant, not the
        // sample time, and they see identical data.
        Assert.Equal(T0, fromA[1].Single().Value.LastChanged);
        Assert.Equal(fromA[1].Single().Value.LastChanged, fromB[1].Single().Value.LastChanged);
        Assert.Equal(T0.AddSeconds(5), fromA[1].Single().Value.SourceTimestamp);
    }

    [Fact]
    public void Observers_still_see_everything_regardless_of_queue_pressure()
    {
        var store = new TagStore();
        var observed = 0;
        store.AddObserver(_ => observed++);
        using var tiny = store.Subscribe("tiny", capacity: 8);

        for (var i = 0; i < 50; i++)
            store.Apply([Read("a", i, i)]);

        Assert.Equal(50, observed);          // lossless path is unaffected
        Assert.True(tiny.Dropped > 0);
    }

    private static List<IReadOnlyList<TagRead>> Drain(TagSubscription subscription)
    {
        var batches = new List<IReadOnlyList<TagRead>>();
        while (subscription.Reader.TryRead(out var batch))
            batches.Add(batch);
        return batches;
    }
}
