namespace HinoGateway.Core;

/// <summary>
/// Everything the engine needs to instantiate and run one device: which driver to use,
/// how often to poll it, driver-specific connection settings, and the tags it owns.
/// </summary>
public sealed record DeviceConfig
{
    /// <summary>Unique device identifier.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Driver type key, matched against <see cref="IDeviceDriverFactory.DriverType"/> (e.g. "Simulator").</summary>
    public required string Driver { get; init; }

    /// <summary>How often the engine polls this device.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Driver-specific connection settings (port, baud, host, unit id, ...).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; }
        = new Dictionary<string, string>();

    /// <summary>The tags belonging to this device.</summary>
    public IReadOnlyList<TagDefinition> Tags { get; init; } = [];
}
