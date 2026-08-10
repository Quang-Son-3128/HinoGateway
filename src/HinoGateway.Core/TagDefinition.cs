namespace HinoGateway.Core;

/// <summary>
/// Static configuration of a single tag: what to read and how to interpret it.
/// The <see cref="Address"/> is driver-specific (e.g. a Modbus register, an S7 DB address,
/// or a simulator waveform name). Kept intentionally small for Phase 0; scaling/deadband/etc.
/// will be added later.
/// </summary>
public sealed record TagDefinition
{
    /// <summary>Globally unique, stable identifier used as the key everywhere downstream.</summary>
    public required string Id { get; init; }

    /// <summary>Human-friendly display name.</summary>
    public required string Name { get; init; }

    /// <summary>Driver-specific address string.</summary>
    public required string Address { get; init; }

    public DataType DataType { get; init; } = DataType.Float64;

    /// <summary>Optional engineering unit for display (e.g. "kg", "°C").</summary>
    public string? Unit { get; init; }

    /// <summary>Optional free-text description of what this tag measures/does.</summary>
    public string? Description { get; init; }

    /// <summary>Linear scaling applied to raw numeric reads: engineering = raw * Scale + Offset.</summary>
    public double Scale { get; init; } = 1.0;

    public double Offset { get; init; } = 0.0;
}
