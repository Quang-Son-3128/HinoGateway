using System.Collections.Concurrent;
using System.Threading.Channels;
using HinoGateway.Core;

namespace HinoGateway.Engine;

/// <summary>
/// Thread-safe holder of the latest <see cref="TagValue"/> for every tag, and the fan-out point for
/// everything that wants to hear about changes.
///
/// Each consumer gets its <em>own</em> bounded queue via <see cref="Subscribe"/>. That matters: a
/// single shared channel would have consumers stealing batches from each other — the operator UI and
/// an upstream publisher would each receive only part of the data. Per-subscription queues also let
/// each consumer choose its own policy: the UI wants the freshest values and can drop stale batches,
/// while a northbound link would rather keep the oldest and be told what it lost.
///
/// Nothing here ever blocks a poll loop; the authoritative latest state is always available from
/// <see cref="Snapshot"/> regardless of what any queue did.
/// </summary>
public sealed class TagStore
{
    private readonly ConcurrentDictionary<string, TagValue> _latest = new();
    private readonly int _defaultCapacity;

    // Copy-on-write: Apply iterates this without locking, subscribe/unsubscribe replace it.
    private volatile TagSubscription[] _subscriptions = Array.Empty<TagSubscription>();
    private readonly object _subscriptionGate = new();

    // Synchronous observers, registered once at composition time. Used by consumers that must not
    // miss a single sample (the alarm engine): unlike the queues, nothing here is ever dropped.
    // Observers therefore run on the polling thread and must stay cheap and non-blocking.
    private readonly List<Action<IReadOnlyList<TagRead>>> _observers = new();

    public TagStore(int updateQueueCapacity = 1024) => _defaultCapacity = Math.Max(8, updateQueueCapacity);

    /// <summary>
    /// Open an independent stream of update batches.
    /// </summary>
    /// <param name="name">Consumer name, for diagnostics ("signalr", "iec104", ...).</param>
    /// <param name="capacity">Queue depth; defaults to the store's configured depth.</param>
    /// <param name="dropOldest">
    /// True (default) discards the stalest batch when the consumer falls behind — right for a live UI.
    /// False keeps what is queued and drops the newest arrivals instead, which suits a consumer that
    /// must forward what it already accepted. Either way the count is reported, never hidden.
    /// </param>
    public TagSubscription Subscribe(string name, int? capacity = null, bool dropOldest = true)
    {
        var subscription = new TagSubscription(this, name, capacity ?? _defaultCapacity, dropOldest);
        lock (_subscriptionGate)
            _subscriptions = _subscriptions.Append(subscription).ToArray();
        return subscription;
    }

    internal void Unsubscribe(TagSubscription subscription)
    {
        lock (_subscriptionGate)
            _subscriptions = _subscriptions.Where(s => !ReferenceEquals(s, subscription)).ToArray();
    }

    /// <summary>Open subscriptions and how much each has dropped — a health signal worth watching.</summary>
    public IReadOnlyList<(string Name, int Capacity, long Dropped)> SubscriptionStats =>
        _subscriptions.Select(s => (s.Name, s.Capacity, s.Dropped)).ToList();

    /// <summary>
    /// Register a lossless synchronous consumer of every applied batch (e.g. alarm evaluation).
    /// Call during composition, before the workers start. An exception thrown by an observer is
    /// swallowed by <see cref="Apply"/> so a faulty consumer can never break a poll loop.
    /// </summary>
    public void AddObserver(Action<IReadOnlyList<TagRead>> observer)
    {
        lock (_observers)
            _observers.Add(observer);
    }

    /// <summary>
    /// Apply a batch of fresh reads: update latest state and publish to consumers. Maintains
    /// <see cref="TagValue.LastChanged"/> — a sample whose value and quality equal the previous
    /// one keeps the previous LastChanged instant (the value didn't actually change).
    /// </summary>
    public void Apply(IReadOnlyList<TagRead> reads)
    {
        if (reads.Count == 0)
            return;

        var enriched = new List<TagRead>(reads.Count);
        foreach (var read in reads)
        {
            var value = read.Value;
            if (_latest.TryGetValue(read.TagId, out var prev)
                && prev.Quality == value.Quality
                && Equals(prev.Value, value.Value))
            {
                value = value with { LastChanged = prev.LastChanged };
            }
            _latest[read.TagId] = value;
            enriched.Add(read with { Value = value });
        }

        // Lossless consumers first (alarm evaluation must see every sample), then the queues.
        // A misbehaving observer is isolated so it cannot stall the poll loop.
        foreach (var observer in _observers)
        {
            try
            {
                observer(enriched);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _observerErrors);
                LastObserverError = ex.Message;
            }
        }

        // Every subscriber gets the same batch in its own queue; none of these calls can block.
        foreach (var subscription in _subscriptions)
            subscription.Publish(enriched);
    }

    /// <summary>
    /// Mark a set of tags as Bad quality (e.g. when their device faults), so that downstream
    /// consumers immediately stop trusting the last-known values.
    /// </summary>
    public void MarkBad(IEnumerable<TagDefinition> tags, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var batch = new List<TagRead>();
        foreach (var tag in tags)
            batch.Add(new TagRead(tag.Id, TagValue.Bad(reason, now)));
        // Apply handles the latest-state update and keeps LastChanged stable while the same
        // Bad reason repeats.
        Apply(batch);
    }

    public bool TryGet(string tagId, out TagValue value) => _latest.TryGetValue(tagId, out value!);

    private long _observerErrors;

    /// <summary>Diagnostics: how many times a synchronous observer threw, and the last message.</summary>
    public long ObserverErrors => Interlocked.Read(ref _observerErrors);

    public string? LastObserverError { get; private set; }

    /// <summary>Drop cached values for tags no longer configured (e.g. after a reload), so removed
    /// tags don't linger as ghosts in snapshots.</summary>
    public void RetainOnly(IEnumerable<string> tagIds)
    {
        var keep = tagIds as HashSet<string> ?? new HashSet<string>(tagIds);
        foreach (var key in _latest.Keys.ToList())
            if (!keep.Contains(key))
                _latest.TryRemove(key, out _);
    }

    /// <summary>Point-in-time copy of every known tag value (used to seed a newly connected client).</summary>
    public IReadOnlyDictionary<string, TagValue> Snapshot() => new Dictionary<string, TagValue>(_latest);
}

/// <summary>
/// One consumer's independent view of the tag stream. Dispose to stop receiving (a subscription left
/// open by a stopped consumer would otherwise keep filling and reporting drops forever).
/// </summary>
public sealed class TagSubscription : IDisposable
{
    private readonly TagStore _store;
    private readonly Channel<IReadOnlyList<TagRead>> _channel;
    private readonly bool _dropOldest;
    private long _dropped;
    private bool _disposed;

    internal TagSubscription(TagStore store, string name, int capacity, bool dropOldest)
    {
        _store = store;
        Name = name;
        Capacity = capacity;
        _dropOldest = dropOldest;

        // Deliberately NOT one of the channel's own Drop* modes: with those, TryWrite always reports
        // success even when it silently threw something away, so a "dropped" counter built on them
        // would sit at zero forever. Wait mode makes a full queue observable (TryWrite returns false)
        // while we still never block — we do the dropping ourselves, and count it.
        _channel = Channel.CreateBounded<IReadOnlyList<TagRead>>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public string Name { get; }
    public int Capacity { get; }

    /// <summary>Batches discarded because this consumer could not keep up.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public ChannelReader<IReadOnlyList<TagRead>> Reader => _channel.Reader;

    internal void Publish(IReadOnlyList<TagRead> batch)
    {
        if (_channel.Writer.TryWrite(batch))
            return;

        // The queue is full — this consumer is behind. Never block the polling thread for it.
        Interlocked.Increment(ref _dropped);
        if (!_dropOldest)
            return;

        // Keep-newest: evict the stalest batch and retry once. A racing writer may take the slot we
        // freed, in which case this batch is the one dropped — already counted either way.
        _channel.Reader.TryRead(out _);
        _channel.Writer.TryWrite(batch);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _store.Unsubscribe(this);
        _channel.Writer.TryComplete();
    }
}
