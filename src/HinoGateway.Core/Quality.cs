namespace HinoGateway.Core;

/// <summary>
/// Data quality of a tag value, in the spirit of the OPC UA status code.
/// A value must never be presented as trustworthy unless its quality is <see cref="Good"/>.
/// </summary>
public enum Quality
{
    /// <summary>The value is invalid or stale (device fault, comms loss, decode error).</summary>
    Bad = 0,

    /// <summary>The value may be usable but is not fully trustworthy (e.g. last-known / initial).</summary>
    Uncertain = 1,

    /// <summary>The value was read successfully from the device at <c>SourceTimestamp</c>.</summary>
    Good = 2,
}
