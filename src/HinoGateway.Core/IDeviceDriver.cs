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

    /// <summary>
    /// Break off whatever is in flight, right now, from another thread.
    ///
    /// <para>
    /// Honouring a <see cref="CancellationToken"/> is not enough for a protocol library that never
    /// took one. NModbus reads with a socket timeout and no token, so a poll aimed at an unplugged
    /// PLC sits inside the library for <c>TimeoutMs × (Retries + 1)</c> — up to ten minutes at the
    /// settings the factory used to accept — while the engine waits ten seconds and then declares
    /// the worker abandoned, keeping its thread, driver and link and asking for a process restart.
    /// A reload during that window cost the whole gateway.
    /// </para>
    ///
    /// <para>
    /// The way out is not a token the library will ignore, but the socket underneath it: close the
    /// handle and the pending read fails immediately. So this is called <em>before</em> the stop
    /// deadline starts, it must not block, and unlike <see cref="DisconnectAsync"/> it is called
    /// concurrently with a poll that is still running — which is the entire point, and the one place
    /// a driver has to be thread-safe. Best-effort: never throw. The default does nothing, which is
    /// right for a driver whose calls are genuinely cancellable.
    /// </para>
    /// </summary>
    Task AbortAsync() => Task.CompletedTask;
}

/// <summary>
/// The timing contract between the engine and its drivers, in one place so that a driver's own
/// validation can be written against the same numbers the engine enforces rather than a copy of them.
/// </summary>
public static class DriverLimits
{
    /// <summary>
    /// How long a reload or a shutdown waits for a poll loop to stop before giving up on it and
    /// reporting an abandoned worker.
    /// </summary>
    public static readonly TimeSpan StopDeadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Refuse a per-request budget that cannot finish inside <see cref="StopDeadline"/>.
    ///
    /// <para>
    /// <see cref="IDeviceDriver.AbortAsync"/> is what actually rescues a stuck poll, and with it a
    /// long timeout is survivable. This check is the second line: a driver that does not implement
    /// abort, or a socket that does not die when closed, puts us straight back to abandoned workers
    /// — and the setting that gets there is one an engineer raises in good faith on a slow radio
    /// link. Better to refuse it at Apply, with the arithmetic shown, than at three in the morning.
    /// </para>
    /// </summary>
    public static string? CheckCancellationBudget(string timeoutKey, int timeoutMs, string retriesKey, int retries)
    {
        var attempts = Math.Max(0, retries) + 1;
        var worstCaseMs = (long)timeoutMs * attempts;
        if (worstCaseMs <= (long)StopDeadline.TotalMilliseconds)
            return null;

        // A driver without a retry setting gets the arithmetic left out rather than shown against a
        // key it does not have; being told to lower 'Retries' when there is no such setting sends an
        // engineer looking for something that was never there.
        var arithmetic = attempts == 1
            ? $"'{timeoutKey}' is {worstCaseMs} ms"
            : $"'{timeoutKey}' × ('{retriesKey}' + 1) is {worstCaseMs} ms";
        var remedy = attempts == 1 ? $"Lower '{timeoutKey}'." : $"Lower '{timeoutKey}' or '{retriesKey}'.";

        return $"{arithmetic}, but one request must be able to give up within {StopDeadline.TotalSeconds:0} s " +
               $"or a reload cannot stop this connection and the gateway has to be restarted. {remedy}";
    }
}

/// <summary>
/// Optional capability for a driver that can tell a transient request failure from proof that its
/// underlying connection is gone. Fatal transport failures bypass the configured consecutive-failure
/// tolerance because waiting through more polls on a dead socket only delays recovery.
/// </summary>
public interface ITransportFaultClassifier
{
    bool IsTransportFault(Exception exception);
}
