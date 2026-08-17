using System.Collections.Concurrent;
using HinoGateway.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// <see cref="TagDefinition.Monotonic"/> was offered on the tag editor, stored, exported and
/// documented for months while nothing anywhere acted on it — an operator could tick "cumulative
/// counter" on an energy register and be protected by precisely nothing. These pin the behaviour it
/// now has.
/// </summary>
public class MonotonicCounterTests
{
    private const string Tag = "Meter/Energy";

    private static DeviceConfig Device(bool monotonic) => new()
    {
        DeviceId = "MTR-1",
        Driver = "Scripted",
        ScanInterval = TimeSpan.FromMilliseconds(20),
        Tags =
        [
            new TagDefinition
            {
                Id = Tag, Name = "Energy", Address = "1", DataType = DataType.Float64,
                Monotonic = monotonic,
            },
        ],
    };

    private static async Task<List<TagValue>> RunAsync(bool monotonic, params double[] readings)
    {
        var store = new TagStore();
        var seen = new BlockingCollection<TagValue>();
        store.AddObserver(batch =>
        {
            foreach (var read in batch.Where(r => r.TagId == Tag))
                seen.Add(read.Value);
        });

        var driver = new ScriptedDriver(readings);
        var engine = new DeviceEngine([new ScriptedDriverFactory(driver)], store, NullLoggerFactory.Instance);
        await engine.LoadAsync([Device(monotonic)]);
        engine.StartAll();

        var collected = new List<TagValue>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (collected.Count < readings.Length && DateTime.UtcNow < deadline)
        {
            if (seen.TryTake(out var value, TimeSpan.FromMilliseconds(200)))
                collected.Add(value);
        }

        await engine.DisposeAsync();
        Assert.Equal(readings.Length, collected.Count);
        return collected;
    }

    [Fact]
    public async Task A_counter_that_drops_is_published_Uncertain_with_the_reason()
    {
        var values = await RunAsync(monotonic: true, 1000, 1010, 4, 1020);

        Assert.Equal(Quality.Good, values[0].Quality);
        Assert.Equal(Quality.Good, values[1].Quality);

        // The drop is not a smaller measurement, it is a bad read — and it says so.
        Assert.Equal(Quality.Uncertain, values[2].Quality);
        Assert.Contains("1010", values[2].StatusText);

        // The peak was not moved by the reading that was not believed.
        Assert.Equal(Quality.Good, values[3].Quality);
    }

    [Fact]
    public async Task Three_drops_in_a_row_are_accepted_as_a_replaced_meter()
    {
        var values = await RunAsync(monotonic: true, 5000, 7, 8, 9, 10);

        Assert.Equal(Quality.Good, values[0].Quality);
        Assert.Equal(Quality.Uncertain, values[1].Quality);
        Assert.Equal(Quality.Uncertain, values[2].Quality);

        // Three in a row cannot be a transient decode error; refusing forever would leave the tag
        // dead until somebody noticed.
        Assert.Equal(Quality.Good, values[3].Quality);
        Assert.Equal(Quality.Good, values[4].Quality);
    }

    [Fact]
    public async Task A_tag_that_is_not_marked_monotonic_is_left_alone()
    {
        var values = await RunAsync(monotonic: false, 1000, 4);

        Assert.All(values, v => Assert.Equal(Quality.Good, v.Quality));
        Assert.Equal(4d, values[1].Value);
    }

    private sealed class ScriptedDriver(double[] readings) : IDeviceDriver
    {
        private int _index;

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task WriteAsync(string tagId, object value, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<IReadOnlyList<TagRead>> PollAsync(CancellationToken ct)
        {
            // Past the end of the script the last value repeats, so the loop cannot fault the device
            // while the test is still draining what it has already produced.
            var index = Math.Min(_index++, readings.Length - 1);
            IReadOnlyList<TagRead> reads =
                [new(Tag, TagValue.Good(readings[index], DateTimeOffset.UtcNow))];
            return Task.FromResult(reads);
        }
    }

    private sealed class ScriptedDriverFactory(ScriptedDriver driver) : IDeviceDriverFactory
    {
        public string DriverType => "Scripted";
        public IDeviceDriver Create(DeviceConfig config) => driver;
    }
}
