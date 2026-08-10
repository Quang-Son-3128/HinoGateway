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

    /// <summary>
    /// How many consecutive failed polls are tolerated before the link is dropped and the tags are
    /// given up on. 1 = drop on the first failure.
    ///
    /// <para>
    /// Reconstructed 2026-08-10 (the file was lost); the default of 3 is the one the storage layer
    /// documents for project files written before the setting existed. It exists because a single
    /// missed poll on a radio or 4G link is normal weather, while tearing the connection down and
    /// marking every tag Bad for it is not.
    /// </para>
    /// </summary>
    public int FailureThreshold { get; init; } = 3;

    /// <summary>Driver-specific connection settings (port, baud, host, unit id, ...).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; }
        = new Dictionary<string, string>();

    /// <summary>The tags belonging to this device.</summary>
    public IReadOnlyList<TagDefinition> Tags { get; init; } = [];
}
