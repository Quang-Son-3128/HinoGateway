namespace HinoGateway.Core;

/// <summary>
/// What produces a tag's value. Until now every tag was read from a device; these two additions are
/// what a gateway needs to stop being a pure protocol converter.
///
/// <para>
/// The distinction is deliberately on the <em>tag</em> and not on a connection. A memory tag has no
/// endpoint to configure, no link to drop and no scan to fail, so giving it a connection would mean
/// inventing a device that is permanently "Connected" and asking an engineer to create one before
/// they can hold a setpoint. The engine therefore runs device tags through
/// <c>DeviceWorker</c> and these two through <c>InternalTagEngine</c> — different producers, one
/// tag database, one <c>TagStore</c>, and every consumer downstream (alarms, IEC-104, MQTT, the UI)
/// unable to tell the difference.
/// </para>
///
/// Never renumber: these values are stored in <c>hino.json</c> and in <c>.hino</c> exports, and
/// <see cref="Device"/> is 0 so every project written before this existed loads as it always did.
/// </summary>
public enum TagKind
{
    /// <summary>Read from (and written to) a device by a protocol driver. The original kind.</summary>
    Device = 0,

    /// <summary>
    /// Holds whatever is written to it and nothing else — a setpoint, an operator note, a mode
    /// selector, a scratch value an expression reads back. The gateway is the only thing that can
    /// change it, so it is the one tag kind whose value is always exactly what someone asked for.
    /// </summary>
    Memory = 1,

    /// <summary>
    /// Computed from other tags and from time, by the expression in
    /// <see cref="TagDefinition.Expression"/>. Read-only: writing to a derived value has no meaning
    /// (the next evaluation would overwrite it).
    /// </summary>
    Expression = 2,

    /// <summary>
    /// Energy (or any other quantity) accumulated over a calendar period, from a device's own
    /// cumulative counter: today's kWh, yesterday's, this month's.
    ///
    /// <para>
    /// It is a separate kind rather than a formula because the answer is a <b>subtraction</b> —
    /// counter now minus counter at the start of the period — and the only thing that has to survive
    /// a power cut is the baseline. An expression that added <c>P × dt</c> every second would instead
    /// have to survive every second of the day, and one bad reading would be wrong forever. Read-only:
    /// the number is derived, and a device counter that kept counting while the gateway was off is
    /// the authority on it.
    /// </para>
    /// </summary>
    Accumulator = 3,
}

/// <summary>When an <see cref="TagKind.Expression"/> tag is recomputed.</summary>
public enum EvalMode
{
    /// <summary>
    /// On a fixed cadence, from <see cref="TagDefinition.ScanInterval"/>. Predictable cost and a
    /// predictable timestamp, and the right default: a value recomputed every second is what almost
    /// every trend, alarm and northbound report actually wants.
    /// </summary>
    Scan = 0,

    /// <summary>
    /// The moment any tag it reads changes. Costs nothing while its inputs sit still and reacts
    /// within one poll of the source instead of within one scan of its own — worth it for an
    /// interlock or a fast alarm, and wasteful for a value nobody looks at between reports.
    /// </summary>
    OnChange = 1,
}

public static class TagKinds
{
    /// <summary>
    /// Is this tag produced by the gateway itself rather than by a driver? Such tags have no
    /// connection, no address and no scaling, and are run by the internal engine.
    /// </summary>
    public static bool IsInternal(this TagKind kind) =>
        kind is TagKind.Memory or TagKind.Expression or TagKind.Accumulator;

    /// <summary>Can an operator (or a northbound command) write to a tag of this kind?</summary>
    public static bool IsWritable(this TagKind kind) => kind is TagKind.Device or TagKind.Memory;

    /// <summary>
    /// Which internal producer runs this kind. Both are fed by the same store and look identical from
    /// the outside, but they are separate engines: the expression engine sorts a dependency graph,
    /// the accumulator engine keeps durable baselines. Handing one an entry meant for the other is a
    /// mistake with no symptom until the tag simply never produces a value.
    /// </summary>
    public static bool IsExpressionEngine(this TagKind kind) => kind is TagKind.Memory or TagKind.Expression;
}
