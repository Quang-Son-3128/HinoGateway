using HinoGateway.Core;
using HinoGateway.Server;
using HinoGateway.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// The failures found in the 2026-08-17 review that had one thing in common: the gateway carried on
/// looking healthy. Each of these was a swallowed error, a backup that was written and never read,
/// or a limit that was never checked.
/// </summary>
public class ResilienceRegressionTests
{
    // =====================================================================================
    //  A reading that is not a number is not a good reading
    // =====================================================================================

    private static TagDefinition Tag(double scale = 1.0, double offset = 0.0) => new()
    {
        Id = "Plant/Temp", Name = "Temp", Address = "40001",
        DataType = DataType.Float32, Scale = scale, Offset = offset,
    };

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_value_that_is_not_finite_cannot_stay_good(double raw)
    {
        // Every comparison against NaN is false, so a high-limit alarm on this tag would never fire:
        // the point reads healthy and in range precisely because its value is meaningless.
        var result = Scaling.Apply(Tag(), TagValue.Good(raw, DateTimeOffset.UtcNow));

        Assert.Equal(Quality.Bad, result.Quality);
        Assert.Null(result.Value);
        Assert.NotNull(result.StatusText);
    }

    [Fact]
    public void Scaling_that_overflows_to_infinity_is_reported_as_such()
    {
        var result = Scaling.Apply(Tag(scale: 1e308), TagValue.Good(1e308, DateTimeOffset.UtcNow));

        Assert.Equal(Quality.Bad, result.Quality);
        Assert.Contains("overflow", result.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_ordinary_reading_is_untouched()
    {
        var result = Scaling.Apply(Tag(scale: 0.1), TagValue.Good(500, DateTimeOffset.UtcNow));

        Assert.Equal(Quality.Good, result.Quality);
        Assert.Equal(50.0, Assert.IsType<double>(result.Value), 10);
    }

    // =====================================================================================
    //  A history frame that could not be written must not report success
    // =====================================================================================

    [Fact]
    public async Task A_history_append_that_fails_is_raised_rather_than_counted_and_forgotten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-hist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var history = new JsonlSampleHistory(directory, new SampleHistoryOptions(),
                NullLogger<JsonlSampleHistory>.Instance);

            // Stand in for a full or read-only disk: the day's file cannot be opened for append
            // because a directory is sitting where it belongs.
            var blocked = Path.Combine(directory, $"hist-{DateTimeOffset.Now:yyyyMMdd}.jsonl");
            Directory.CreateDirectory(blocked);

            var frame = new HistoryFrame(DateTimeOffset.Now,
                new Dictionary<string, HistorySample> { ["Plant/Power"] = new(1.0, Quality.Good) });

            // The caller used to receive a completed task and keep sending its heartbeat, so the
            // gateway reported itself healthy while keeping no record at all.
            await Assert.ThrowsAnyAsync<Exception>(() => history.AppendAsync(frame));
            Assert.Equal(1, history.Stats.Dropped);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // =====================================================================================
    //  A backup that is written must also be read
    // =====================================================================================

    [Fact]
    public void Retained_values_are_recovered_from_the_backup_when_the_primary_is_corrupt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-retain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "hino.retain.json");

            // Two flushes, so that the second leaves the first behind as .bak. Dispose forces the
            // pending one, which is how the store reaches the disk at shutdown.
            using (var store = new RetainedValueStore(path, NullLogger.Instance, TimeSpan.Zero))
                store.Save("Plant/Setpoint", 42.0);
            using (var store = new RetainedValueStore(path, NullLogger.Instance, TimeSpan.Zero))
                store.Save("Plant/Setpoint", 43.0);
            Assert.True(File.Exists(path + ".bak"), "the sidecar must keep a backup, as the project file does");

            // A power cut during the publish: the primary is half-written, the backup is intact.
            File.WriteAllText(path, "{\"format\":\"hino-retained\",\"values\":{\"Plant/Set");

            using var reopened = new RetainedValueStore(path, NullLogger.Instance, TimeSpan.Zero);

            Assert.True(reopened.TryGet("Plant/Setpoint", out var value));
            Assert.Equal(42.0, Convert.ToDouble(value));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // =====================================================================================
    //  A project has to be a size one gateway can actually run
    // =====================================================================================

    private static ProjectValidator Validator() =>
        new([new HinoGateway.Drivers.Simulator.SimulatorDriverFactory()]);

    [Fact]
    public void A_project_with_more_connections_than_one_gateway_takes_is_refused()
    {
        var project = new HinoProject
        {
            Devices = Enumerable.Range(0, ProjectValidator.MaxDevices + 1)
                .Select(i => new DeviceDto { Id = $"dev{i}", Driver = "Simulator", ScanIntervalMs = 60_000 })
                .ToList(),
        };

        var problems = Validator().Validate(project);

        // Named with the count, because an operator who imported the wrong file recognises the
        // mistake from the number rather than from the word "too many".
        Assert.Contains(problems, p => p.Contains("connections") && p.Contains($"{ProjectValidator.MaxDevices + 1:N0}"));
    }

    [Fact]
    public void A_project_that_asks_for_more_reads_per_second_than_the_machine_has_is_refused()
    {
        // Well under the device limit, and 100 × 20/s = 2,000 reads a second on an industrial PC.
        var project = new HinoProject
        {
            Devices = Enumerable.Range(0, 100)
                .Select(i => new DeviceDto { Id = $"dev{i}", Driver = "Simulator", ScanIntervalMs = 20 })
                .ToList(),
        };

        var problems = Validator().Validate(project);

        Assert.Contains(problems, p => p.Contains("reads per second"));
    }

    [Fact]
    public void An_ordinary_project_passes_the_capacity_checks()
    {
        var project = new HinoProject
        {
            Devices = Enumerable.Range(0, 20)
                .Select(i => new DeviceDto { Id = $"dev{i}", Driver = "Simulator", ScanIntervalMs = 1000 })
                .ToList(),
        };

        var problems = Validator().Validate(project);

        Assert.DoesNotContain(problems, p => p.Contains("at most") || p.Contains("reads per second"));
    }
}
