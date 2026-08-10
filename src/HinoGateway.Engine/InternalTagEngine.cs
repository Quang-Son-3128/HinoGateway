using HinoGateway.Core;
using HinoGateway.Expressions;
using Microsoft.Extensions.Logging;

namespace HinoGateway.Engine;

/// <summary>
/// Produces the values of the tags that have no device behind them: <see cref="TagKind.Memory"/> tags
/// hold what is written to them, <see cref="TagKind.Expression"/> tags are computed from other tags.
///
/// <para>
/// It is the counterpart of <see cref="DeviceWorker"/> — a second kind of producer feeding the one
/// <see cref="TagStore"/> — and everything downstream (alarms, IEC-104, MQTT, the UI) is unable to
/// tell which of the two produced a value. That is the whole point: a plant total computed from three
/// inverters must be alarmable, publishable and displayable exactly like a measured one.
/// </para>
///
/// <para>
/// Everything runs on one dedicated thread (see <see cref="WorkerThread"/>), which is what lets the
/// evaluation order be a plain sorted list rather than a concurrency problem. Expressions are
/// evaluated in <b>topological order</b>, so an expression reading another expression sees this
/// tick's value and not the previous one's — a chain of derived values settles in a single pass
/// instead of lagging one scan per link.
/// </para>
/// </summary>
public sealed class InternalTagEngine : IAsyncDisposable
{
    private readonly TagStore _tags;
    private readonly ILogger _logger;
    private readonly IRetainedValueStore? _retained;
    private readonly Func<DateTimeOffset> _clock;

    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);

    /// <summary>Expression entries in evaluation order. Fixed after <see cref="Load"/>.</summary>
    private readonly List<Entry> _ordered = new();

    /// <summary>Which expression entries read a given tag — the reverse of the dependency graph.</summary>
    private readonly Dictionary<string, List<Entry>> _dependents = new(StringComparer.Ordinal);

    private readonly List<(string TagId, string Reason)> _faults = new();

    private TimeSpan _tick = TimeSpan.Zero;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private TagSubscription? _subscription;
    private long _evaluations;
    private bool _started;

    /// <param name="clock">
    /// Injectable so tests can drive time. Local time on purpose: every time function in a formula —
    /// <c>hour()</c>, <c>dayofweek()</c> — answers about the wall clock an operator reads.
    /// </param>
    public InternalTagEngine(
        TagStore tags,
        ILogger logger,
        IRetainedValueStore? retained = null,
        Func<DateTimeOffset>? clock = null)
    {
        _tags = tags;
        _logger = logger;
        _retained = retained;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Every internal tag this engine runs.</summary>
    public IReadOnlyCollection<string> TagIds => _byId.Keys;

    public bool Owns(string tagId) => _byId.ContainsKey(tagId);

    /// <summary>
    /// Tags this engine cannot run — a formula that does not compile, or one caught in a circular
    /// reference. They are reported so an apply can tell the operator, and are published Bad with the
    /// reason so they do not simply sit blank on the Live page.
    /// </summary>
    public IReadOnlyList<(string TagId, string Reason)> Faults => _faults;

    public InternalTagStats Stats => new(
        _byId.Count,
        _byId.Values.Count(e => e.Definition.Kind == TagKind.Memory),
        _ordered.Count,
        _faults.Count,
        Interlocked.Read(ref _evaluations));

    // =====================================================================================
    //  Loading
    // =====================================================================================

    /// <summary>
    /// Compile the formulas, work out the evaluation order, and give every tag a starting value.
    /// Called once, before <see cref="Start"/>; a configuration change builds a new engine rather than
    /// mutating this one, exactly like the device runtime.
    /// </summary>
    public void Load(IReadOnlyList<TagDefinition> definitions)
    {
        var now = _clock();
        var seeds = new List<TagRead>();

        foreach (var definition in definitions)
        {
            if (!definition.Kind.IsInternal())
                continue;
            if (_byId.ContainsKey(definition.Id))
                continue;   // duplicate ids are refused by validation; ignoring is the safe reaction

            var entry = new Entry(definition);
            _byId[definition.Id] = entry;

            if (definition.Kind == TagKind.Memory)
            {
                if (SeedMemory(entry, now) is { } seed)
                    seeds.Add(seed);
                continue;
            }

            try
            {
                entry.Compiled = ExpressionCompiler.Compile(definition.Expression ?? "");
                entry.Memory = entry.Compiled.CreateMemory();
                entry.Period = EvaluationPeriod(definition);
                entry.DueAt = now;

                // An on-change formula that reads no tags at all has nothing that could ever wake it,
                // so it is evaluated once and then stands still — which is the honest consequence of
                // asking for on-change on a formula with no inputs, rather than a permanently blank
                // tag. Everything else waits for an input to move; nothing is evaluated against
                // devices that have not been polled yet.
                entry.Dirty = definition.EvalMode == EvalMode.OnChange
                              && entry.Compiled.ReferencedTags.Count == 0;
            }
            catch (ExpressionSyntaxException ex)
            {
                // One broken formula must cost exactly one tag. This mirrors the connection rule the
                // gateway already follows for a mistyped device address.
                Fault(entry, ex.Describe());
            }
            _ordered.Add(entry);
        }

        SortAndDetectCycles();
        BuildDependents();

        foreach (var entry in _ordered.Where(e => e.Fault is not null))
            seeds.Add(new TagRead(entry.Definition.Id, TagValue.Bad(entry.Fault!, now)));

        _tick = ChooseTick();

        if (seeds.Count > 0)
            _tags.Apply(seeds);

        // Values kept for tags that are no longer retentive (or no longer exist) are dropped here,
        // after seeding has had its chance to read them.
        _retained?.RetainOnly(_byId.Values
            .Where(e => e.Definition.Kind == TagKind.Memory && e.Definition.Retentive)
            .Select(e => e.Definition.Id));

        if (_byId.Count > 0)
            _logger.LogInformation(
                "Internal tags loaded: {Memory} memory, {Expressions} expression ({Faulted} unusable), tick {Tick}",
                Stats.MemoryTags, Stats.ExpressionTags, _faults.Count, _tick);
    }

    /// <summary>
    /// A memory tag's starting value, in the order that keeps an operator's decisions alive:
    /// <list type="number">
    /// <item>what the tag already holds — a configuration apply rebuilds this engine, and the value
    /// an operator wrote ten seconds ago must survive that. Without this step, pressing "Apply" would
    /// silently reset every setpoint in the plant;</item>
    /// <item>the retained value from disk, for a tag configured to survive a restart;</item>
    /// <item>the configured initial value;</item>
    /// <item>nothing — the tag starts Bad with "never written", which is honest. Serving 0 for a
    /// setpoint nobody has set would be indistinguishable from someone having chosen 0.</item>
    /// </list>
    /// </summary>
    private TagRead? SeedMemory(Entry entry, DateTimeOffset now)
    {
        var definition = entry.Definition;

        if (_tags.TryGet(definition.Id, out var existing) && existing.Value is not null)
            return null;

        if (definition.Retentive && _retained is not null
            && _retained.TryGet(definition.Id, out var saved) && saved is not null
            && InternalTagValues.TryCoerce(definition.DataType, saved, out var restored, out _))
            return new TagRead(definition.Id, TagValue.Good(restored!, now));

        if (InternalTagValues.TryParseInitial(definition.InitialValue, definition.DataType, out var initial, out var error))
            return new TagRead(definition.Id, TagValue.Good(initial!, now));

        return new TagRead(definition.Id, error is null
            ? TagValue.Bad("never written", now)
            : TagValue.Bad($"initial value: {error}", now));
    }

    private static TimeSpan EvaluationPeriod(TagDefinition definition)
    {
        var period = definition.ScanInterval ?? InternalTagDefaults.ExpressionScanInterval;
        return period < InternalTagDefaults.MinimumExpressionScanInterval
            ? InternalTagDefaults.MinimumExpressionScanInterval
            : period;
    }

    /// <summary>
    /// Kahn's algorithm over the expression tags: dependencies first, and whatever is left over when
    /// the queue empties is part of a cycle.
    ///
    /// <para>
    /// A cycle is refused rather than broken arbitrarily. <c>A = B + 1</c> with <c>B = A + 1</c> has
    /// no answer, and the alternative — evaluating one of them against last pass's value — turns a
    /// configuration mistake into a slowly drifting number that looks like a measurement.
    /// </para>
    /// </summary>
    private void SortAndDetectCycles()
    {
        var indegree = _ordered.ToDictionary(e => e, _ => 0);
        var edges = new Dictionary<Entry, List<Entry>>();

        foreach (var entry in _ordered)
        {
            if (entry.Compiled is null)
                continue;
            foreach (var source in entry.Compiled.ReferencedTags)
            {
                if (!_byId.TryGetValue(source, out var producer) || producer.Definition.Kind != TagKind.Expression)
                    continue;   // a device or memory tag: an input, not a dependency to order
                if (ReferenceEquals(producer, entry))
                    continue;   // self-reference is caught below as a cycle of one
                if (!edges.TryGetValue(producer, out var list))
                    edges[producer] = list = new List<Entry>();
                list.Add(entry);
                indegree[entry]++;
            }

            if (entry.Compiled.ReferencedTags.Contains(entry.Definition.Id))
                Fault(entry, $"circular reference: {{{entry.Definition.Id}}} reads itself");
        }

        var ready = new Queue<Entry>(_ordered.Where(e => indegree[e] == 0));
        var sorted = new List<Entry>(_ordered.Count);
        while (ready.Count > 0)
        {
            var entry = ready.Dequeue();
            sorted.Add(entry);
            if (!edges.TryGetValue(entry, out var dependents))
                continue;
            foreach (var dependent in dependents)
            {
                if (--indegree[dependent] == 0)
                    ready.Enqueue(dependent);
            }
        }

        var placed = new HashSet<Entry>(sorted);
        var looping = _ordered.Where(e => !placed.Contains(e)).ToList();
        if (looping.Count > 0)
        {
            var names = string.Join(" → ", looping.Select(e => $"{{{e.Definition.Id}}}"));
            foreach (var entry in looping)
            {
                Fault(entry, $"circular reference between {names}");
                sorted.Add(entry);   // kept in the list so it is reported, never evaluated
            }
        }

        _ordered.Clear();
        _ordered.AddRange(sorted);
    }

    private void BuildDependents()
    {
        foreach (var entry in _ordered)
        {
            if (entry.Compiled is null)
                continue;
            foreach (var source in entry.Compiled.ReferencedTags)
            {
                if (!_dependents.TryGetValue(source, out var list))
                    _dependents[source] = list = new List<Entry>();
                list.Add(entry);
            }
        }
    }

    /// <summary>
    /// How often the loop wakes: the shortest period any scan-mode expression asked for. Tags due
    /// less often are simply not evaluated on most ticks, which costs a comparison each.
    /// </summary>
    private TimeSpan ChooseTick()
    {
        var scanning = _ordered
            .Where(e => e.Fault is null && e.Definition.EvalMode == EvalMode.Scan)
            .Select(e => e.Period)
            .ToList();
        return scanning.Count == 0 ? TimeSpan.Zero : scanning.Min();
    }

    private void Fault(Entry entry, string reason)
    {
        entry.Fault = reason;
        _faults.Add((entry.Definition.Id, reason));
        _logger.LogWarning("Internal tag {TagId} is unusable — {Reason}", entry.Definition.Id, reason);
    }

    // =====================================================================================
    //  Running
    // =====================================================================================

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        var needsTimer = _tick > TimeSpan.Zero;
        var needsChanges = _ordered.Any(e => e.Fault is null && e.Definition.EvalMode == EvalMode.OnChange);
        if (!needsTimer && !needsChanges)
            return;   // memory tags alone need no loop: a write publishes itself

        if (needsChanges)
        {
            // Keep-oldest with a generous queue: missing a batch means missing the change that was
            // supposed to trigger an evaluation, and unlike the UI this consumer cannot catch up from
            // the next snapshot.
            _subscription = _tags.Subscribe("internal-tags", capacity: 2048, dropOldest: false);
        }

        _cancellation = new CancellationTokenSource();
        _loop = WorkerThread.Run("internal-tags", () => RunAsync(_cancellation.Token),
            ex => _logger.LogError(ex, "Internal tag engine callback failed"));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = _tick > TimeSpan.Zero ? new PeriodicTimer(_tick) : null;
        Task? tick = null;
        Task? incoming = null;

        try
        {
            // Anything already owing an evaluation before a single tick has passed (see Load).
            if (_ordered.Any(e => e.Dirty))
                RunPass(scanTick: false);

            while (!ct.IsCancellationRequested)
            {
                if (timer is not null)
                    tick ??= timer.WaitForNextTickAsync(ct).AsTask();
                if (_subscription is not null)
                    incoming ??= _subscription.Reader.WaitToReadAsync(ct).AsTask();

                var waiting = new List<Task>(2);
                if (tick is not null) waiting.Add(tick);
                if (incoming is not null) waiting.Add(incoming);
                if (waiting.Count == 0)
                    return;

                var completed = await Task.WhenAny(waiting).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return;

                var scanTick = ReferenceEquals(completed, tick);
                if (scanTick)
                {
                    tick = null;
                }
                else
                {
                    incoming = null;
                    MarkChanged();
                }

                RunPass(scanTick);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // The loop dying silently would leave every expression tag frozen at its last value with
            // nothing to say so — the failure mode this gateway has already been bitten by once.
            _logger.LogCritical(ex, "Internal tag engine stopped unexpectedly; expression tags are no longer updating");
            var now = _clock();
            _tags.Apply(_ordered
                .Select(e => new TagRead(e.Definition.Id, TagValue.Bad($"internal tag engine stopped: {ex.Message}", now)))
                .ToList());
        }
    }

    /// <summary>Note which on-change tags have had one of their inputs refreshed.</summary>
    private void MarkChanged()
    {
        if (_subscription is null)
            return;

        while (_subscription.Reader.TryRead(out var batch))
        {
            foreach (var read in batch)
            {
                if (!LooksChanged(read.Value) || !_dependents.TryGetValue(read.TagId, out var dependents))
                    continue;
                foreach (var dependent in dependents)
                {
                    if (dependent.Definition.EvalMode == EvalMode.OnChange && dependent.Fault is null)
                        dependent.Dirty = true;
                }
            }
        }
    }

    /// <summary>
    /// Did this sample actually change anything? <see cref="TagStore.Apply"/> keeps the previous
    /// <see cref="TagValue.LastChanged"/> when the value and quality repeat, so a sample whose
    /// LastChanged is older than its source timestamp is a re-read of the same number. Erring towards
    /// "changed" only costs an extra evaluation; erring the other way would miss one.
    /// </summary>
    private static bool LooksChanged(TagValue value) => value.LastChanged >= value.SourceTimestamp;

    /// <summary>
    /// One evaluation pass over the expression tags, in dependency order.
    /// </summary>
    private void RunPass(bool scanTick)
    {
        var now = _clock();
        var values = new PassValues(_tags);
        List<TagRead>? batch = null;

        foreach (var entry in _ordered)
        {
            if (entry.Fault is not null)
                continue;

            var scan = entry.Definition.EvalMode == EvalMode.Scan;
            var due = scan ? scanTick && now >= entry.DueAt : entry.Dirty;
            if (!due)
                continue;

            if (scan)
                entry.DueAt = now + entry.Period;
            entry.Dirty = false;

            var read = Evaluate(entry, now, values);
            values.Set(read.TagId, read.Value);
            (batch ??= new List<TagRead>()).Add(read);

            // A dependent in on-change mode is woken here rather than one pass later, so a chain of
            // derived values is consistent within the tick that produced it.
            if (Changed(entry, read.Value) && _dependents.TryGetValue(entry.Definition.Id, out var dependents))
            {
                foreach (var dependent in dependents)
                {
                    if (dependent.Definition.EvalMode == EvalMode.OnChange && dependent.Fault is null)
                        dependent.Dirty = true;
                }
            }
            entry.Last = read.Value;
        }

        if (batch is not null)
            _tags.Apply(batch);
    }

    private static bool Changed(Entry entry, TagValue value) =>
        entry.Last is not { } previous || previous.Quality != value.Quality || !Equals(previous.Value, value.Value);

    private TagRead Evaluate(Entry entry, DateTimeOffset now, PassValues values)
    {
        var definition = entry.Definition;
        Interlocked.Increment(ref _evaluations);

        ExpressionValue result;
        try
        {
            result = entry.Compiled!.Evaluate(new EvaluationContext(values, now, entry.Memory));
        }
        catch (Exception ex)
        {
            // Individual nodes already trap their own failures; this is the last net, so that a bug in
            // the evaluator costs one tag rather than the whole loop.
            _logger.LogError(ex, "Expression tag {TagId} failed to evaluate", definition.Id);
            return new TagRead(definition.Id, TagValue.Bad($"evaluation failed: {ex.Message}", now));
        }

        if (result.Value is null)
            return new TagRead(definition.Id,
                TagValue.Bad(result.Status ?? "the expression produced no value", now));

        if (!InternalTagValues.TryCoerce(definition.DataType, result.Value, out var typed, out var error))
            return new TagRead(definition.Id, TagValue.Bad(error ?? $"the result does not fit {definition.DataType}", now));

        // The evaluation instant is the source timestamp: for a computed tag, the gateway *is* the
        // device, and this is what makes age() and the Live page's "last change" mean something.
        return new TagRead(definition.Id, result.Quality == Quality.Good
            ? TagValue.Good(typed!, now)
            : new TagValue
            {
                Value = typed,
                Quality = result.Quality,
                StatusText = result.Status,
                SourceTimestamp = now,
                LastChanged = now,
            });
    }

    // =====================================================================================
    //  Writing
    // =====================================================================================

    /// <summary>
    /// Write to a memory tag. Published immediately rather than on the next tick: an operator who
    /// moves a setpoint expects to see it move, and anything that reads the tag — an on-change
    /// expression, an alarm, a northbound point — should act on the new value now.
    /// </summary>
    public Task WriteAsync(string tagId, object value, CancellationToken ct = default)
    {
        if (!_byId.TryGetValue(tagId, out var entry))
            throw new KeyNotFoundException($"No internal tag '{tagId}'.");

        if (entry.Definition.Kind != TagKind.Memory)
            throw new NotSupportedException(
                $"Tag '{tagId}' is calculated by an expression; the next evaluation would overwrite anything written to it.");

        if (!InternalTagValues.TryCoerce(entry.Definition.DataType, value, out var typed, out var error))
            throw new ArgumentException($"Cannot write to '{tagId}': {error}");

        var now = _clock();
        _tags.Apply(new[] { new TagRead(tagId, TagValue.Good(typed!, now)) });

        if (entry.Definition.Retentive)
            _retained?.Save(tagId, typed);

        return Task.CompletedTask;
    }

    // =====================================================================================
    //  Lifetime
    // =====================================================================================

    public async ValueTask DisposeAsync()
    {
        _cancellation?.Cancel();
        if (_loop is not null)
        {
            try
            {
                // Bounded like the device workers': a loop that will not stop must not hold up a
                // reload. It is a single cooperative loop with no driver call in it, so this deadline
                // is only ever reached if something is very wrong.
                await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError("Internal tag engine did not stop within 5 s");
            }
            catch (OperationCanceledException)
            {
            }
        }

        _subscription?.Dispose();
        _cancellation?.Dispose();
    }

    /// <summary>
    /// The values computed so far in the current pass, layered over the store. Without this an
    /// expression reading another expression would see the previous pass's number even though its
    /// input was recomputed moments earlier in the same pass — and one batch is published at the end,
    /// so consumers see the whole consistent set at once.
    /// </summary>
    private sealed class PassValues : ITagValueSource
    {
        private readonly TagStore _store;
        private readonly Dictionary<string, TagValue> _fresh = new(StringComparer.Ordinal);

        public PassValues(TagStore store) => _store = store;

        public void Set(string tagId, TagValue value) => _fresh[tagId] = value;

        public bool TryGet(string tagId, out TagValue value) =>
            _fresh.TryGetValue(tagId, out value!) || _store.TryGet(tagId, out value);
    }

    private sealed class Entry
    {
        public Entry(TagDefinition definition) => Definition = definition;

        public TagDefinition Definition { get; }
        public CompiledExpression? Compiled { get; set; }
        public ExpressionMemory? Memory { get; set; }

        /// <summary>Set once when the tag cannot run at all; it is then never evaluated.</summary>
        public string? Fault { get; set; }

        public TimeSpan Period { get; set; }
        public DateTimeOffset DueAt { get; set; }

        /// <summary>On-change mode: one of its inputs moved and it owes an evaluation.</summary>
        public bool Dirty { get; set; }

        /// <summary>What it last produced, to tell a real change from a recomputed identical value.</summary>
        public TagValue? Last { get; set; }
    }
}

/// <summary>Counters for the internal tag engine, for the health endpoint and the UI.</summary>
public sealed record InternalTagStats(
    int Total,
    int MemoryTags,
    int ExpressionTags,
    int FaultedTags,
    long Evaluations);
