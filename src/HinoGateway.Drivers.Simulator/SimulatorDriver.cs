using HinoGateway.Core;

namespace HinoGateway.Drivers.Simulator;

/// <summary>
/// A driver that fabricates values instead of talking to hardware — the reference implementation
/// of <see cref="IDeviceDriver"/> and the vehicle for exercising the engine (scan cadence, quality,
/// fault isolation) without any device. The waveform is chosen per tag by its <c>Address</c>:
/// <c>sine</c>, <c>ramp</c>, <c>random</c>, <c>bool</c>, or a numeric constant.
/// </summary>
public sealed class SimulatorDriver : IDeviceDriver
{
    private readonly DeviceConfig _config;
    private readonly Random _rng = new();
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

    public SimulatorDriver(DeviceConfig config) => _config = config;

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task WriteAsync(string tagId, object value, CancellationToken ct) =>
        throw new NotSupportedException("The simulator driver is read-only.");

    public Task<IReadOnlyList<TagRead>> PollAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = (now - _start).TotalSeconds;

        var reads = new List<TagRead>(_config.Tags.Count);
        foreach (var tag in _config.Tags)
        {
            object value = Generate(tag.Address, elapsed);
            reads.Add(new TagRead(tag.Id, TagValue.Good(value, now)));
        }

        return Task.FromResult<IReadOnlyList<TagRead>>(reads);
    }

    private object Generate(string address, double t)
    {
        switch (address.Trim().ToLowerInvariant())
        {
            case "sine":
                return Math.Round(50 + 50 * Math.Sin(t * 2 * Math.PI / 10), 3); // 0..100, 10s period
            case "ramp":
                return Math.Round(t % 100, 3);                                   // 0..100 sawtooth
            case "random":
                return Math.Round(_rng.NextDouble() * 100, 3);
            case "bool":
                return (int)(t / 5) % 2 == 0;                                    // toggles every 5s
            default:
                return double.TryParse(address, out var c) ? c : 0d;            // constant fallback
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
