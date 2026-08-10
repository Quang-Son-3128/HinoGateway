namespace HinoGateway.Core;

/// <summary>
/// What one physical request a driver issues is costing: which range it covers, how many tags depend
/// on it, and how it has been behaving. On a real plant this is the difference between "the device is
/// slow" and "this one block of registers is being refused" — the engineer needs to see it per block,
/// not just per device.
/// </summary>
public sealed record DriverBlockStat(
    // Protocol area, e.g. "Holding registers".
    string Area,
    // First address in the block and how many addresses it covers.
    int Start,
    int Count,
    // How many configured tags are served by this one request.
    int Tags,
    long Reads,
    long Errors,
    double LastLatencyMs,
    string? LastError,
    // Set while the block is being skipped after the device refused it.
    bool Quarantined);

/// <summary>
/// Optional driver capability: report how the reads are actually being issued. A driver that groups
/// points into block reads implements this so its plan and per-block health are observable instead
/// of being an internal guess.
/// </summary>
public interface IDriverDiagnostics
{
    IReadOnlyList<DriverBlockStat> Blocks { get; }
}
