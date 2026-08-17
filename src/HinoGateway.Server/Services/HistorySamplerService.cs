using HinoGateway.Core;
using HinoGateway.Engine;

namespace HinoGateway.Server.Services;

/// <summary>
/// Writes one <see cref="HistoryFrame"/> per interval from whatever the <see cref="TagStore"/> is
/// holding at that instant.
///
/// <para>
/// Frames are taken on the <b>boundary</b> — 00:00:00, 00:01:00, … — not "every 60 seconds from
/// whenever the gateway happened to start. That is what makes the midnight frame exist at all, and
/// the midnight frame is the one an accumulator needs to recover a day's baseline after a power cut.
/// A tick that is missed because the machine was busy is skipped rather than written late, so a
/// timestamp in this file always means the instant it says.
/// </para>
/// </summary>
public sealed class HistorySamplerService : BackgroundService
{
    private const string Component = "history-sampler";

    private readonly TagStore _tags;
    private readonly ISampleHistory _history;
    private readonly ComponentHealthRegistry _health;
    private readonly ILogger<HistorySamplerService> _logger;
    private readonly SampleHistoryOptions _options;
    private readonly Func<DateTimeOffset> _clock;

    public HistorySamplerService(TagStore tags, ISampleHistory history, SampleHistoryOptions options,
        ComponentHealthRegistry health, ILogger<HistorySamplerService> logger,
        Func<DateTimeOffset>? clock = null)
    {
        _tags = tags;
        _history = history;
        _options = options;
        _health = health;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Sampling period, clamped to the range 378/QĐ-EVN allows on the slow end.</summary>
    private TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(_options.IntervalSeconds, 1, 300));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            // Deliberately not registered with the health registry: a component that will never
            // send a heartbeat would go stale and take /health/ready red for being switched off.
            _logger.LogWarning("Sample history is disabled. Decision 378/QĐ-EVN requires local " +
                               "storage at 5-minute resolution kept for at least 7 days.");
            return;
        }

        _logger.LogInformation("History sampler writing one frame every {Interval}", Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _health.Running(Component);
                await SampleLoopAsync(stoppingToken);
                if (!stoppingToken.IsCancellationRequested)
                    throw new InvalidOperationException("History sampler loop returned unexpectedly.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Same rule as every other supervised loop here: a loop that dies silently leaves the
                // gateway looking healthy while the record the utility will ask for stops being kept.
                _health.Failed(Component, ex);
                _logger.LogError(ex, "History sampler stopped; restarting");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task SampleLoopAsync(CancellationToken ct)
    {
        var interval = Interval;
        var next = NextBoundary(_clock(), interval);

        // One second at a time so the heartbeat stays inside the registry's staleness window however
        // long the sampling interval is — a five-minute interval must not look like a dead component.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            _health.Heartbeat(Component);

            var now = _clock();
            if (now < next)
                continue;

            // Stamped with the boundary that has just been crossed, computed from `now` rather than
            // from the boundary we were waiting for. After a suspend — or a machine so busy that
            // several intervals went by — those two differ, and dating current readings to an instant
            // ten minutes ago is exactly the kind of quiet wrongness an accumulator would then build
            // a day's energy on. Missed frames stay missed; only real ones are written.
            var boundary = NextBoundary(now, interval).Add(-interval);
            await _history.AppendAsync(new HistoryFrame(boundary, Collect()), ct);

            next = boundary.Add(interval);
            _health.Heartbeat(Component);
        }
    }

    /// <summary>The next instant divisible by the interval, in local time — where "midnight" lives.</summary>
    internal static DateTimeOffset NextBoundary(DateTimeOffset now, TimeSpan interval)
    {
        var ticks = interval.Ticks;
        var aligned = new DateTimeOffset(now.DateTime.Date, now.Offset)
            .AddTicks((now.DateTime.TimeOfDay.Ticks / ticks) * ticks);
        return aligned <= now ? aligned.AddTicks(ticks) : aligned;
    }

    /// <summary>
    /// Every tag that currently has a number. A tag whose value is neither numeric nor boolean, and
    /// one that has never been read, is simply absent from the frame — see <see cref="ISampleHistory"/>.
    /// </summary>
    private Dictionary<string, HistorySample> Collect()
    {
        var snapshot = _tags.Snapshot();
        var values = new Dictionary<string, HistorySample>(snapshot.Count, StringComparer.Ordinal);
        foreach (var (tagId, value) in snapshot)
        {
            if (TryNumber(value.Value) is { } number)
                values[tagId] = new HistorySample(number, value.Quality);
        }
        return values;
    }

    internal static double? TryNumber(object? value) => value switch
    {
        null => null,
        bool flag => flag ? 1d : 0d,
        double d => double.IsFinite(d) ? d : null,
        float f => float.IsFinite(f) ? f : null,
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal =>
            Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
        // A numeric string is still a number to a trend; anything else genuinely is not.
        string text => double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
            ? parsed
            : null,
        _ => null,
    };
}
