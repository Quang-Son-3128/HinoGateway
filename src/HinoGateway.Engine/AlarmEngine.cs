using System.Threading.Channels;
using HinoGateway.Core;
using Microsoft.Extensions.Logging;

namespace HinoGateway.Engine;

/// <summary>
/// Evaluates the configured alarms against every tag sample and owns their live state
/// (active / acknowledged / cleared-unacknowledged) plus a journal stream.
///
/// Evaluation is <em>synchronous</em>, driven by <see cref="TagStore.AddObserver"/>: alarms must
/// never miss a sample, so they cannot ride the lossy realtime channel the UI uses. The work per
/// sample is a dictionary lookup plus a numeric compare, and the whole evaluation is guarded by a
/// single short-lived lock — cheap enough to run on the polling threads.
///
/// On/off delays are measured against the clock at evaluation time rather than with timers, so no
/// extra scheduler is involved and the behaviour is reproducible (tests inject the clock).
/// </summary>
public sealed class AlarmEngine
{
    private readonly ILogger<AlarmEngine> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private Dictionary<string, Rule> _rules = new(StringComparer.Ordinal);
    private Dictionary<string, List<Rule>> _byTag = new(StringComparer.Ordinal);

    // Journal lines waiting to be recorded / pushed. Bounded so an alarm storm cannot grow memory
    // without limit. Wait mode — not DropWrite — because the channel's own drop modes report success
    // from TryWrite even when they discarded something, which would leave the counter below stuck at
    // zero; here a full queue is visible and the loss is counted and logged instead of hidden.
    private readonly Channel<AlarmEvent> _events = Channel.CreateBounded<AlarmEvent>(
        new BoundedChannelOptions(8192)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private long _droppedEvents;

    /// <param name="clock">
    /// Time source for the on/off delays. Defaults to the wall clock; tests substitute it so delay
    /// behaviour can be verified without sleeping.
    /// </param>
    public AlarmEngine(ILogger<AlarmEngine> logger, Func<DateTimeOffset>? clock = null)
    {
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Stream of alarm journal lines (activated / cleared / acknowledged).</summary>
    public ChannelReader<AlarmEvent> Events => _events.Reader;

    /// <summary>Journal lines discarded because the queue was full (should stay 0).</summary>
    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    /// <summary>
    /// Replace the definition set. Live state (active since, acknowledgement, activation count) is
    /// carried over for alarms whose id, tag and condition are unchanged, so editing one alarm — or
    /// reloading after a tag rename — never silently clears an outstanding alarm.
    /// </summary>
    public void Reload(IEnumerable<AlarmDefinition> definitions)
    {
        lock (_gate)
        {
            var previous = _rules;
            var rules = new Dictionary<string, Rule>(StringComparer.Ordinal);
            var byTag = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);

            foreach (var def in definitions)
            {
                if (string.IsNullOrWhiteSpace(def.Id) || rules.ContainsKey(def.Id))
                    continue;

                var rule = previous.TryGetValue(def.Id, out var old)
                    && old.Definition.Condition == def.Condition
                    && string.Equals(old.Definition.TagId, def.TagId, StringComparison.Ordinal)
                        ? old.WithDefinition(def)
                        : new Rule(def);

                rules[def.Id] = rule;
                if (!byTag.TryGetValue(def.TagId, out var list))
                    byTag[def.TagId] = list = new List<Rule>();
                list.Add(rule);
            }

            _rules = rules;
            _byTag = byTag;
            _logger.LogInformation("AlarmEngine loaded {Count} alarm definition(s) on {Tags} tag(s)",
                rules.Count, byTag.Count);
        }
    }

    /// <summary>Observer entry point: evaluate a batch of fresh tag reads.</summary>
    public void OnReads(IReadOnlyList<TagRead> reads)
    {
        var now = _clock();
        lock (_gate)
        {
            foreach (var read in reads)
            {
                if (!_byTag.TryGetValue(read.TagId, out var rules))
                    continue;
                foreach (var rule in rules)
                    Evaluate(rule, read.Value, now);
            }
        }
    }

    /// <summary>Current alarm states — by default only what an operator needs to see.</summary>
    public IReadOnlyList<AlarmState> States(bool includeNormal = false)
    {
        lock (_gate)
        {
            var list = new List<AlarmState>(_rules.Count);
            foreach (var rule in _rules.Values)
            {
                var status = rule.Status;
                if (!includeNormal && status == AlarmStatus.Normal)
                    continue;
                list.Add(rule.ToState(status));
            }
            // Most urgent first, then most recent.
            return list
                .OrderByDescending(a => a.Status == AlarmStatus.Active)
                .ThenByDescending(a => a.Severity)
                .ThenByDescending(a => a.ActiveSince ?? DateTimeOffset.MinValue)
                .ToList();
        }
    }

    public AlarmSummary Summary()
    {
        lock (_gate)
        {
            var active = 0;
            var unacked = 0;
            AlarmSeverity? highest = null;
            var bySeverity = new Dictionary<AlarmSeverity, int>();

            foreach (var rule in _rules.Values)
            {
                var status = rule.Status;
                if (rule.Active)
                {
                    active++;
                    bySeverity[rule.Definition.Severity] = bySeverity.GetValueOrDefault(rule.Definition.Severity) + 1;
                    if (highest is null || rule.Definition.Severity > highest)
                        highest = rule.Definition.Severity;
                }
                if (status is AlarmStatus.Active or AlarmStatus.ClearedUnacknowledged)
                    unacked++;
            }

            var counts = bySeverity
                .OrderByDescending(kv => kv.Key)
                .Select(kv => new SeverityCount(kv.Key, kv.Value))
                .ToList();
            return new AlarmSummary(_rules.Count, active, unacked, highest, counts);
        }
    }

    /// <summary>Acknowledge one alarm. Returns false when it is unknown or has nothing outstanding.</summary>
    public bool Acknowledge(string alarmId, string? user)
    {
        var now = _clock();
        lock (_gate)
        {
            if (!_rules.TryGetValue(alarmId, out var rule))
                return false;
            return AcknowledgeLocked(rule, user, now);
        }
    }

    /// <summary>Acknowledge everything outstanding. Returns how many alarms were acknowledged.</summary>
    public int AcknowledgeAll(string? user)
    {
        var now = _clock();
        lock (_gate)
        {
            var count = 0;
            foreach (var rule in _rules.Values)
                if (AcknowledgeLocked(rule, user, now))
                    count++;
            return count;
        }
    }

    private bool AcknowledgeLocked(Rule rule, string? user, DateTimeOffset now)
    {
        if (rule.Acked || rule.Status == AlarmStatus.Normal)
            return false;

        rule.Acked = true;
        rule.AckedAt = now;
        rule.AckedBy = user;
        Emit(rule, AlarmEventType.Acknowledged, now, user);
        return true;
    }

    // --- evaluation ----------------------------------------------------------

    private void Evaluate(Rule rule, TagValue sample, DateTimeOffset now)
    {
        var def = rule.Definition;
        if (!def.Enabled)
            return;

        rule.Value = sample.Value;

        var met = ConditionMet(def, sample, rule.Active);
        if (met is null)
        {
            // Undecidable (untrusted or non-numeric value): freeze the state and reset the delay
            // timers so a gap in the data can never be counted towards an on/off delay.
            rule.PendingOnSince = null;
            rule.PendingOffSince = null;
            return;
        }

        if (met.Value)
        {
            rule.PendingOffSince = null;
            if (rule.Active)
                return;

            rule.PendingOnSince ??= now;
            if (now - rule.PendingOnSince.Value < TimeSpan.FromMilliseconds(Math.Max(0, def.OnDelayMs)))
                return;

            rule.Active = true;
            rule.ActiveSince = now;
            rule.ClearedAt = null;
            rule.Acked = false;
            rule.AckedAt = null;
            rule.AckedBy = null;
            rule.Triggered = true;
            rule.ActivationCount++;
            rule.PendingOnSince = null;
            Emit(rule, AlarmEventType.Activated, now, null);
        }
        else
        {
            rule.PendingOnSince = null;
            if (!rule.Active)
                return;

            rule.PendingOffSince ??= now;
            if (now - rule.PendingOffSince.Value < TimeSpan.FromMilliseconds(Math.Max(0, def.OffDelayMs)))
                return;

            rule.Active = false;
            rule.ClearedAt = now;
            rule.PendingOffSince = null;
            Emit(rule, AlarmEventType.Cleared, now, null);
        }
    }

    /// <summary>
    /// Is the alarm condition currently met? <c>null</c> means "cannot tell" — the quality is not
    /// Good or the value isn't of a comparable shape — and leaves the alarm state untouched.
    /// A value that cannot be trusted must never be allowed to clear (or raise) a level alarm;
    /// use a <see cref="AlarmCondition.BadQuality"/> alarm to be told about comms loss instead.
    /// </summary>
    internal static bool? ConditionMet(AlarmDefinition def, TagValue sample, bool active)
    {
        if (def.Condition == AlarmCondition.BadQuality)
            return sample.Quality != Quality.Good;

        if (sample.Quality != Quality.Good)
            return null;

        if (def.Condition == AlarmCondition.Digital)
            return TryTruth(sample.Value, out var on) ? on == def.TriggerOnTrue : null;

        if (!TryNumber(sample.Value, out var x))
            return null;

        // While active the limit is relaxed by the hysteresis band, so a value hovering on the
        // limit cannot chatter. With hysteresis 0 both sides use the limit itself.
        var h = Math.Abs(def.Hysteresis);
        return def.Condition switch
        {
            AlarmCondition.Hi or AlarmCondition.HiHi => active ? x >= def.Limit - h : x >= def.Limit,
            AlarmCondition.Lo or AlarmCondition.LoLo => active ? x <= def.Limit + h : x <= def.Limit,
            _ => null,
        };
    }

    private static bool TryTruth(object? value, out bool on)
    {
        switch (value)
        {
            case bool b:
                on = b;
                return true;
            case null:
                on = false;
                return false;
            default:
                if (TryNumber(value, out var x))
                {
                    on = x != 0;
                    return true;
                }
                on = false;
                return false;
        }
    }

    private static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case null:
                number = 0;
                return false;
            case bool b:
                number = b ? 1 : 0;
                return true;
            case double d:
                number = d;
                return !double.IsNaN(d);
            case float f:
                number = f;
                return !float.IsNaN(f);
            case IConvertible c when value is not string:
                try
                {
                    number = c.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    number = 0;
                    return false;
                }
            default:
                number = 0;
                return false;
        }
    }

    private void Emit(Rule rule, AlarmEventType type, DateTimeOffset at, string? user)
    {
        var hasNumber = TryNumber(rule.Value, out var numeric);
        var evt = new AlarmEvent(
            Id: 0,
            Timestamp: at,
            AlarmId: rule.Definition.Id,
            Name: rule.Definition.Name,
            TagId: rule.Definition.TagId,
            Condition: rule.Definition.Condition,
            Severity: rule.Definition.Severity,
            Type: type,
            Value: hasNumber ? numeric : null,
            Message: rule.Definition.Message,
            User: user);

        if (!_events.Writer.TryWrite(evt))
        {
            var dropped = Interlocked.Increment(ref _droppedEvents);
            if (dropped == 1 || dropped % 100 == 0)
                _logger.LogWarning("Alarm journal queue full — {Dropped} event(s) dropped", dropped);
        }
    }

    /// <summary>Mutable live state of one alarm. Only touched under <see cref="_gate"/>.</summary>
    private sealed class Rule
    {
        public Rule(AlarmDefinition definition) => Definition = definition;

        public AlarmDefinition Definition { get; private set; }

        public bool Active;
        public bool Acked;
        public bool Triggered;          // has activated at least once
        public DateTimeOffset? ActiveSince;
        public DateTimeOffset? ClearedAt;
        public DateTimeOffset? AckedAt;
        public string? AckedBy;
        public object? Value;
        public long ActivationCount;
        public DateTimeOffset? PendingOnSince;
        public DateTimeOffset? PendingOffSince;

        public Rule WithDefinition(AlarmDefinition def)
        {
            Definition = def;
            return this;
        }

        public AlarmStatus Status
        {
            get
            {
                if (Active)
                    return Acked ? AlarmStatus.ActiveAcknowledged : AlarmStatus.Active;
                if (Definition.RequiresAck && Triggered && !Acked)
                    return AlarmStatus.ClearedUnacknowledged;
                return AlarmStatus.Normal;
            }
        }

        public AlarmState ToState(AlarmStatus status) => new(
            AlarmId: Definition.Id,
            Name: Definition.Name,
            TagId: Definition.TagId,
            Condition: Definition.Condition,
            Severity: Definition.Severity,
            Status: status,
            Limit: Definition.Limit,
            Message: Definition.Message,
            ActiveSince: ActiveSince,
            ClearedAt: ClearedAt,
            AckedAt: AckedAt,
            AckedBy: AckedBy,
            Value: Value,
            ActivationCount: ActivationCount);
    }
}

