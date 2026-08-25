using System.Collections.Concurrent;
using System.Threading.Channels;
using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Expressions;

namespace HinoGateway.Server.Services;

/// <summary>
/// Runs a command when its trigger tag changes: computes the script, then writes what it asked for.
///
/// <para>
/// This closes the gap between the utility's control centre and the plant. An IEC-104 setpoint lands
/// in a memory tag — deliberately, because the control centre speaks in percent of the plant and the
/// PLC wants kilowatts in a particular register — and until now the number simply stopped there.
/// </para>
///
/// <para>
/// <b>Every write goes through <see cref="ControlCommandService"/>, never straight to the engine.</b>
/// That is what gives a command the same treatment as an operator pressing a button: the tag's
/// AllowWrite switch and write limits, the per-device queue and its deadline, and a line in the audit
/// trail naming the command that did it. A command is a new way to decide what to write, not a new
/// way to write.
/// </para>
///
/// <para>
/// <b>Commands do not chain.</b> A trigger tag that another command writes to is refused at load and
/// reported, rather than being allowed to start a cascade whose length nobody can read off the
/// configuration. Enforcing it statically rather than by suppressing writes at run time means the
/// answer to "can this loop" is visible on the screen instead of depending on timing.
/// </para>
/// </summary>
public sealed class CommandEngine : BackgroundService
{
    /// <summary>
    /// Triggers waiting to run. Bounded: a command that cannot keep up must be visible as a refusal
    /// rather than as unbounded memory behind a plant that is moving faster than the PLC answers.
    /// </summary>
    private const int QueueCapacity = 256;

    /// <summary>How long the idle loop waits before saying it is still alive. See <see cref="WaitForTriggerAsync"/>.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly DeviceEngine _engine;
    private readonly IConfigStore _store;
    private readonly ControlCommandService _control;
    private readonly ComponentHealthRegistry _health;
    private readonly ILogger<CommandEngine> _logger;

    private readonly Channel<Trigger> _queue = Channel.CreateBounded<Trigger>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly ConcurrentDictionary<string, CommandRunInfo> _lastRun = new(StringComparer.Ordinal);

    /// <summary>Trigger path → the commands on it. Replaced wholesale on reload; never mutated.</summary>
    private volatile IReadOnlyDictionary<string, IReadOnlyList<Loaded>> _byTrigger =
        new Dictionary<string, IReadOnlyList<Loaded>>(StringComparer.Ordinal);

    /// <summary>What each trigger was worth the last time we ran it, so a re-publish is not a trigger.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);

    private long _runs;
    private long _dropped;

    public CommandEngine(DeviceEngine engine, IConfigStore store, ControlCommandService control,
        ComponentHealthRegistry health, ILogger<CommandEngine> logger)
    {
        _engine = engine;
        _store = store;
        _control = control;
        _health = health;
        _logger = logger;

        // A lossless observer rather than a subscription: a subscription drops batches under load,
        // and the batch it drops could be the one carrying the control centre's setpoint. The
        // observer runs inside the poll loop, so it does no more than compare a timestamp and post
        // to a queue.
        _engine.Tags.AddObserver(OnBatch);
    }

    private sealed record Loaded(CommandDto Dto, CompiledScript Script);

    private sealed record Trigger(Loaded Command, TagValue Value);

    public long Runs => Interlocked.Read(ref _runs);

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>The last run of each command, by command id — what the screen shows.</summary>
    public IReadOnlyDictionary<string, CommandRunInfo> LastRuns => _lastRun;

    public CommandRunInfo? LastRun(string commandId) =>
        _lastRun.TryGetValue(commandId, out var info) ? info : null;

    /// <summary>
    /// Load (or reload) the commands from the store. Called at startup and by every apply, for the
    /// same reason the alarm engine is: a tag rename changes the runtime paths a command is bound to.
    /// </summary>
    public async Task<int> ReloadAsync(CancellationToken ct = default)
    {
        var commands = await _store.GetCommandsAsync(ct);
        var loaded = new List<Loaded>();

        foreach (var command in commands.Where(c => c.Enabled))
        {
            if (string.IsNullOrWhiteSpace(command.TriggerTagPath))
            {
                _logger.LogWarning("Command '{Name}' is not running: its trigger tag no longer exists", command.Name);
                continue;
            }
            if (!ScriptCompiler.TryCompile(command.Script, out var compiled, out var error, out _))
            {
                _logger.LogError("Command '{Name}' is not running: {Error}", command.Name, error);
                continue;
            }
            loaded.Add(new Loaded(command, compiled!));
        }

        // Chain refusal, computed over the loaded set: a command whose trigger is written by another
        // command would start a cascade. Both are named in the log, because "my command stopped
        // working" needs to lead to the other command that shadowed it.
        var written = loaded.SelectMany(l => l.Script.WrittenTags).ToHashSet(StringComparer.Ordinal);
        var running = new Dictionary<string, List<Loaded>>(StringComparer.Ordinal);
        foreach (var command in loaded)
        {
            if (written.Contains(command.Dto.TriggerTagPath))
            {
                _logger.LogError(
                    "Command '{Name}' is not running: its trigger tag '{Tag}' is written by another command, "
                    + "and commands do not chain", command.Dto.Name, command.Dto.TriggerTagPath);
                continue;
            }
            if (!running.TryGetValue(command.Dto.TriggerTagPath, out var list))
                running[command.Dto.TriggerTagPath] = list = new List<Loaded>();
            list.Add(command);
        }

        _byTrigger = running.ToDictionary(p => p.Key, p => (IReadOnlyList<Loaded>)p.Value, StringComparer.Ordinal);

        // Seed the "last seen" marks from the current values, so that reloading does not make every
        // command fire once on a value that was already there before the apply. A tag the engine has
        // not produced yet gets no mark at all — OnBatch then treats its first sighting as the seed,
        // which is the case that matters: a memory tag is created a moment after this runs, and
        // without it every restart wrote its trigger's starting value to the plant. On a curtailment
        // command that means the gateway sends a limit of its own accord after a power cut.
        foreach (var path in _byTrigger.Keys)
        {
            if (_engine.Tags.TryGet(path, out var value))
                _lastSeen[path] = value.LastChanged;
            else
                _lastSeen.TryRemove(path, out _);
        }

        _logger.LogInformation("Commands loaded: {Running} running, {Total} configured", loaded.Count, commands.Count);
        return _byTrigger.Values.Sum(v => v.Count);
    }

    /// <summary>
    /// Inside the poll loop: compare, queue, return. A command fires on a <em>change</em>, because
    /// the tag store keeps <see cref="TagValue.LastChanged"/> still when a sample repeats the
    /// previous value — so writing 50 twice runs the command once, which is the behaviour a setpoint
    /// wants.
    /// </summary>
    private void OnBatch(IReadOnlyList<TagRead> reads)
    {
        var byTrigger = _byTrigger;
        if (byTrigger.Count == 0) return;

        foreach (var read in reads)
        {
            if (!byTrigger.TryGetValue(read.TagId, out var commands))
                continue;
            var known = _lastSeen.TryGetValue(read.TagId, out var seen);
            if (known && seen == read.Value.LastChanged)
                continue;
            _lastSeen[read.TagId] = read.Value.LastChanged;

            // The first value ever seen for a trigger is where the tag started, not an order
            // somebody gave. A command runs on a change it witnessed while running.
            if (!known)
                continue;

            // A trigger the gateway does not trust is not an order. A memory tag holds no value at
            // all until something writes one, so without this every command ran once at startup on
            // its own empty trigger and reported a failure nobody had caused.
            if (read.Value.Quality != Quality.Good)
                continue;

            foreach (var command in commands)
            {
                if (!_queue.Writer.TryWrite(new Trigger(command, read.Value)))
                {
                    Interlocked.Increment(ref _dropped);
                    _logger.LogWarning("Command '{Name}' was not run: the command queue is full ({Capacity})",
                        command.Dto.Name, QueueCapacity);
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _health.Running("commands");
        try
        {
            await ReloadAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Commands could not be loaded at startup");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await WaitForTriggerAsync(stoppingToken))
                {
                    while (_queue.Reader.TryRead(out var trigger))
                        await RunAsync(trigger, stoppingToken);
                    _health.Heartbeat("commands");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The loop stays alive. A command that throws must not leave the gateway unable to
                // act on the next order from the control centre.
                _health.Failed("commands", ex);
                _logger.LogError(ex, "The command loop failed; continuing");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                // Shutting down. Whatever the services below were doing when they were disposed is
                // not a fault to report - and throwing here would be reported by the host as a
                // service that failed to stop.
                _logger.LogDebug(ex, "The command loop ended during shutdown");
                return;
            }
        }
    }

    /// <summary>
    /// Waits for the next trigger, but never longer than <see cref="HeartbeatInterval"/>.
    ///
    /// <para>
    /// A plant nobody has sent an order to is the normal state, and heart-beating only after a run
    /// made that state look like a fault: half a minute after every start the operator's screen
    /// carried "commands: No heartbeat for 39 seconds" in red, and /health/ready failed behind it —
    /// so a watchdog would restart a gateway that was working, over and over, because it is still
    /// quiet after the restart. Waiting on a bounded token lets the loop say "still here" while it
    /// waits, at no cost while it is busy.
    /// </para>
    /// </summary>
    private async Task<bool> WaitForTriggerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            heartbeat.CancelAfter(HeartbeatInterval);
            try { return await _queue.Reader.WaitToReadAsync(heartbeat.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { _health.Heartbeat("commands"); }
        }
        return false;
    }

    private async Task RunAsync(Trigger trigger, CancellationToken ct)
    {
        var command = trigger.Command;
        var at = DateTimeOffset.Now;
        // FromTag rather than Good(value): it puts the reading through the same normalisation every
        // formula gets, so an Int16 from a driver arrives as a number rather than as its ToString().
        var input = ExpressionValue.FromTag(command.Dto.TriggerTagPath, trigger.Value);

        var run = command.Script.Run(_engine.Tags, input, at);
        Interlocked.Increment(ref _runs);

        var writes = new List<CommandWriteInfo>();
        foreach (var write in run.Writes)
        {
            if (ct.IsCancellationRequested) break;

            // Through the control service: policy, queue, deadline and audit all apply, and the
            // audit line names the command rather than "api".
            try
            {
                var result = await _control.ExecuteAsync(new ControlCommand(
                    write.TagPath, write.Value!, Source: "Command", Actor: command.Dto.Name, RemoteAddress: null));

                writes.Add(new CommandWriteInfo(write.TagPath, Describe(write.Value),
                    result.Outcome == ControlOutcome.Successful, result.Reason));
            }
            catch (Exception ex)
            {
                // One write failing is reported on the command and the rest are still attempted:
                // stopping half way through a set of setpoints leaves the plant in a state nobody
                // asked for, and the report is what makes that visible either way.
                writes.Add(new CommandWriteInfo(write.TagPath, Describe(write.Value), false, ex.Message));
            }
        }

        var failed = writes.FirstOrDefault(w => !w.Succeeded);
        var info = new CommandRunInfo(command.Dto.Id, at, Describe(trigger.Value.Value), writes,
            run.Completed && failed is null,
            run.Error ?? failed?.Error);
        _lastRun[command.Dto.Id] = info;

        if (info.Succeeded)
            _logger.LogInformation("Command '{Name}' ran on {Value} and wrote {Count} tag(s)",
                command.Dto.Name, info.TriggerValue, writes.Count);
        else
            _logger.LogWarning("Command '{Name}' did not complete: {Error}", command.Dto.Name, info.Error);
    }

    internal static string? Describe(object? value) => value switch
    {
        null => null,
        double d => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => value.ToString(),
    };
}
