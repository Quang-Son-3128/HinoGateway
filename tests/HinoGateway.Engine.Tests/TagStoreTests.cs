using HinoGateway.Core;
using HinoGateway.Engine;
using Xunit;

namespace HinoGateway.Engine.Tests;

public class TagStoreTests
{
    // Safely in the past: MarkBad stamps DateTimeOffset.UtcNow, which must be after T0.
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LastChanged_stays_while_value_repeats_and_moves_when_it_changes()
    {
        var store = new TagStore();

        store.Apply([new TagRead("t", TagValue.Good(42.0, T0))]);
        store.Apply([new TagRead("t", TagValue.Good(42.0, T0.AddSeconds(1)))]);
        store.Apply([new TagRead("t", TagValue.Good(42.0, T0.AddSeconds(2)))]);

        Assert.True(store.TryGet("t", out var same));
        Assert.Equal(T0.AddSeconds(2), same.SourceTimestamp); // freshness advances
        Assert.Equal(T0, same.LastChanged);                   // but the value never changed

        store.Apply([new TagRead("t", TagValue.Good(43.0, T0.AddSeconds(3)))]);
        Assert.True(store.TryGet("t", out var changed));
        Assert.Equal(T0.AddSeconds(3), changed.LastChanged);
    }

    [Fact]
    public void Quality_transition_counts_as_a_change_and_repeated_bad_does_not()
    {
        var store = new TagStore();
        var tag = new TagDefinition { Id = "t", Name = "t", Address = "x" };

        store.Apply([new TagRead("t", TagValue.Good(1.0, T0))]);
        store.MarkBad([tag], "boom");
        Assert.True(store.TryGet("t", out var bad));
        var badSince = bad.LastChanged;
        Assert.Equal(Quality.Bad, bad.Quality);
        Assert.True(badSince > T0); // going Bad IS a change

        store.MarkBad([tag], "boom");
        Assert.True(store.TryGet("t", out var stillBad));
        Assert.Equal(badSince, stillBad.LastChanged); // staying Bad is not
    }
}
