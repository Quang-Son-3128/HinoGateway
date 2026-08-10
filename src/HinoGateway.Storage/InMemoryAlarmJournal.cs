using HinoGateway.Core;

namespace HinoGateway.Storage;

/// <summary>
/// The local alarm trail: a bounded, newest-first ring of recent events.
///
/// The gateway is not the system of record — alarm history belongs to the upstream server, and the
/// events are on their way there (northbound forwarding). What is kept here is only what an
/// engineer standing at the industrial PC needs to answer "what just happened?", so it lives in
/// memory: nothing to install, nothing to fill the disk, nothing to corrupt on a power cut.
/// Oldest lines are dropped once <see cref="Capacity"/> is reached, and the count of dropped lines
/// is reported rather than hidden.
/// </summary>
public sealed class InMemoryAlarmJournal : IAlarmJournal
{
    private readonly object _gate = new();
    private readonly Queue<AlarmEvent> _events;
    private long _nextId = 1;
    private long _dropped;

    public InMemoryAlarmJournal(int capacity = 2000)
    {
        Capacity = Math.Max(50, capacity);
        _events = new Queue<AlarmEvent>(Math.Min(Capacity, 512));
    }

    public int Capacity { get; }

    public (int Count, long Dropped) Stats
    {
        get { lock (_gate) return (_events.Count, _dropped); }
    }

    public Task AppendAsync(IReadOnlyList<AlarmEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0)
            return Task.CompletedTask;

        lock (_gate)
        {
            foreach (var evt in events)
            {
                // Ids are assigned here so the UI has a stable key per line, as the old store did.
                _events.Enqueue(evt with { Id = _nextId++ });
                if (_events.Count > Capacity)
                {
                    _events.Dequeue();
                    _dropped++;
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AlarmEvent>> GetAsync(DateTimeOffset? from, DateTimeOffset? to,
        string? alarmId, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<AlarmEvent> q = _events;
            if (from is not null)
                q = q.Where(e => e.Timestamp >= from.Value);
            if (to is not null)
                q = q.Where(e => e.Timestamp <= to.Value);
            if (!string.IsNullOrWhiteSpace(alarmId))
                q = q.Where(e => e.AlarmId == alarmId);

            IReadOnlyList<AlarmEvent> result = q
                .OrderByDescending(e => e.Id)
                .Take(Math.Clamp(limit, 1, Capacity))
                .ToList();
            return Task.FromResult(result);
        }
    }
}
