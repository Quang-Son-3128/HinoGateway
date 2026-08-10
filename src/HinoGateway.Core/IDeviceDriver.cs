namespace HinoGateway.Core;

/// <summary>
/// A protocol driver for one device. The engine owns scheduling, isolation, retry and health;
/// the driver only implements the protocol. Keep implementations free of their own timers or
/// background threads — the engine calls <see cref="PollAsync"/> on a deterministic cadence.
///
/// A driver instance is used by a single device worker (single-threaded access), so it need not
/// be internally thread-safe. It <em>must</em> honour the <see cref="CancellationToken"/> to
/// support fast, clean shutdown, and should perform block/bulk reads for efficiency.
/// </summary>
public interface IDeviceDriver : IAsyncDisposable
{
    /// <summary>Open the connection. Throw on failure — the engine handles retry/backoff.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// Read all of this device's tags once and return their freshly sampled values.
    /// Throw on a comms error — the engine will fault the device and reconnect.
    /// </summary>
    Task<IReadOnlyList<TagRead>> PollAsync(CancellationToken ct);

    /// <summary>
    /// Write a value to the given tag (control/command). Throw <see cref="NotSupportedException"/>
    /// for read-only drivers. Access is serialized with <see cref="PollAsync"/> by the engine, so
    /// implementations need not be thread-safe.
    /// </summary>
    Task WriteAsync(string tagId, object value, CancellationToken ct);

    /// <summary>Close the connection. Should not throw; best-effort cleanup.</summary>
    Task DisconnectAsync(CancellationToken ct);
}
