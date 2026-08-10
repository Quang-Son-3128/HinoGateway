using HinoGateway.Core;
using Microsoft.Extensions.Logging;

namespace HinoGateway.Engine;

/// <summary>
/// One <see cref="IDeviceTransport"/> per physical link, shared by every connection that names it,
/// reference-counted so the link closes when the last connection lets go.
///
/// <para>
/// ⚠ <b>Reconstructed on 2026-08-10</b> after the source tree was lost. The public shape is fixed by
/// <see cref="DeviceEngine"/> (which survived verbatim): <see cref="Acquire"/> returning a lease,
/// <see cref="Snapshot"/> for the diagnostics endpoint, and asynchronous disposal of the whole
/// registry. The behaviour below is what that call site requires.
/// </para>
///
/// <para>
/// The registry is owned by the engine and not by a runtime snapshot, which is the point: a
/// connection that survives a configuration reload must keep its serial port open. Rebuilding the
/// runtime therefore releases the old leases <em>after</em> taking the new ones, and a link whose
/// count never reaches zero is never closed at all.
/// </para>
/// </summary>
public sealed class TransportRegistry : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public TransportRegistry(ILogger<TransportRegistry> logger) => _logger = logger;

    /// <summary>What each open link is doing, for <c>/api/diagnostics/transports</c>.</summary>
    public IReadOnlyList<TransportInfo> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _entries.Select(e =>
                {
                    var (transactions, queued) = SafeStats(e.Value.Transport);
                    return new TransportInfo(e.Key, e.Value.Users, transactions, queued);
                }).ToList();
            }
        }
    }

    /// <summary>
    /// Take a claim on the link called <paramref name="key"/>, creating it on first use. The
    /// factory runs inside the lock so two connections applied at the same instant cannot both build
    /// one and leave an orphan holding the serial port.
    /// </summary>
    public TransportLease Acquire(string key, Func<IDeviceTransport> factory)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_entries.TryGetValue(key, out var entry))
            {
                var transport = factory()
                    ?? throw new InvalidOperationException($"Driver returned no transport for '{key}'.");
                entry = new Entry(transport);
                _entries[key] = entry;
                _logger.LogInformation("Opened shared link {Key}", key);
            }

            entry.Users++;
            return new TransportLease(this, key, entry.Transport);
        }
    }

    private async ValueTask ReleaseAsync(string key)
    {
        IDeviceTransport? closing = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return;
            if (--entry.Users > 0)
                return;

            _entries.Remove(key);
            closing = entry.Transport;
        }

        // Outside the lock: closing a serial port can block, and nothing else should wait behind it.
        _logger.LogInformation("Closing shared link {Key} — no connection is using it", key);
        await DisposeQuietly(closing, key);
    }

    private async ValueTask DisposeQuietly(IDeviceTransport transport, string key)
    {
        try
        {
            await transport.DisposeAsync();
        }
        catch (Exception ex)
        {
            // A link that will not close must not stop a reload; the process restart that a stuck
            // handle eventually needs is reported through the engine's abandoned-worker counter.
            _logger.LogError(ex, "Shared link {Key} did not close cleanly", key);
        }
    }

    private static (long Transactions, int Queued) SafeStats(IDeviceTransport transport)
    {
        try { return transport.Stats; }
        catch { return (0, 0); }
    }

    public async ValueTask DisposeAsync()
    {
        List<KeyValuePair<string, Entry>> entries;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = _entries.ToList();
            _entries.Clear();
        }

        foreach (var (key, entry) in entries)
            await DisposeQuietly(entry.Transport, key);
    }

    private sealed class Entry(IDeviceTransport transport)
    {
        public IDeviceTransport Transport { get; } = transport;
        public int Users { get; set; }
    }

    /// <summary>
    /// One connection's claim on a shared link. Disposing it decrements the count; the last one out
    /// closes the link. Disposal is idempotent so a failed build path can release without checking.
    /// </summary>
    public sealed class TransportLease : IAsyncDisposable
    {
        private TransportRegistry? _registry;

        internal TransportLease(TransportRegistry registry, string key, IDeviceTransport transport)
        {
            _registry = registry;
            Key = key;
            Transport = transport;
        }

        public string Key { get; }
        public IDeviceTransport Transport { get; }

        public async ValueTask DisposeAsync()
        {
            var registry = Interlocked.Exchange(ref _registry, null);
            if (registry is not null)
                await registry.ReleaseAsync(Key);
        }
    }
}

/// <summary>
/// A shared link as the diagnostics endpoint reports it: how many connections use it, how much it
/// has carried and how many requests are queued for it right now. An empty list is the normal case —
/// it means every connection owns its own link.
/// </summary>
public sealed record TransportInfo(string Key, int Connections, long Transactions, int Queued);
