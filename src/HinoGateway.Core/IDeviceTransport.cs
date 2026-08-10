namespace HinoGateway.Core;

/// <summary>
/// A physical link that several connections share — an RS-485 port with a dozen slaves on it, one
/// socket to a TCP↔RTU gateway.
///
/// <para>
/// ⚠ <b>Reconstructed on 2026-08-10</b> after the source tree was lost. The contract below is the one
/// <c>DeviceEngine</c> and <c>TransportRegistry</c> require; the original may have carried extra
/// members used only by the Modbus RTU driver, which will surface when that driver is rebuilt.
/// </para>
///
/// <para>
/// The reason this exists at all is that RS-485 is a bus: exactly one transaction may be on the wire
/// at a time, whoever it belongs to. Sharing therefore means <em>serialising</em>, not pooling —
/// <see cref="ExecuteAsync{T}"/> takes the link for the duration of one request/response exchange.
/// A protocol whose session can carry several requests at once (OPC UA) should implement this
/// interface directly and lock only around connect/reconnect instead.
/// </para>
/// </summary>
public interface IDeviceTransport : IAsyncDisposable
{
    /// <summary>Identifies the physical link, e.g. a port name or host:port.</summary>
    string Key { get; }

    /// <summary>
    /// Run one exchange with exclusive use of the link. <paramref name="timeout"/> bounds the wait
    /// for the link, not the exchange itself: a caller that never gets to the wire must be told so
    /// rather than queue for ever behind a device that stopped answering.
    /// </summary>
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> exchange, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>How many exchanges this link has carried, and how many are waiting right now.</summary>
    (long Transactions, int Queued) Stats { get; }
}
