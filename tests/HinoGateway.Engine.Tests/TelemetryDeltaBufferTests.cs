using HinoGateway.Core;
using HinoGateway.Engine;

namespace HinoGateway.Engine.Tests;

public sealed class TelemetryDeltaBufferTests
{
    [Fact]
    public void Unchanged_samples_are_suppressed_after_a_successful_send()
    {
        var buffer = new TelemetryDeltaBuffer();
        buffer.Add([Read(1)]);
        var first = buffer.Drain();
        buffer.MarkSent(first);

        buffer.Add([Read(1, DateTimeOffset.UtcNow.AddSeconds(1))]);

        Assert.Empty(buffer.Drain());
    }

    [Fact]
    public void A_value_that_changes_back_inside_one_window_leaves_no_delta()
    {
        var buffer = new TelemetryDeltaBuffer();
        buffer.Add([Read(1)]);
        buffer.MarkSent(buffer.Drain());

        buffer.Add([Read(2), Read(1)]);

        Assert.Empty(buffer.Drain());
    }

    [Fact]
    public void A_failed_send_is_retried_on_the_next_sample()
    {
        var buffer = new TelemetryDeltaBuffer();
        buffer.Add([Read(1)]);
        Assert.Single(buffer.Drain()); // deliberately do not MarkSent

        buffer.Add([Read(1)]);

        Assert.Single(buffer.Drain());
    }

    [Fact]
    public void Removed_tags_are_forgotten_across_reloads()
    {
        var buffer = new TelemetryDeltaBuffer();
        buffer.Add([Read(1)]);
        buffer.MarkSent(buffer.Drain());

        buffer.RetainOnly(new HashSet<string>(StringComparer.Ordinal));
        buffer.Add([Read(1)]);

        Assert.Single(buffer.Drain());
    }

    private static TagRead Read(int value, DateTimeOffset? at = null)
    {
        var timestamp = at ?? DateTimeOffset.UtcNow;
        return new TagRead("plant/tag", TagValue.Good(value, timestamp));
    }
}
