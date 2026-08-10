namespace HinoGateway.Core;

/// <summary>
/// Creates <see cref="IDeviceDriver"/> instances for a given driver type. Each driver package
/// (Simulator, Modbus, S7, ...) registers one factory; the engine selects the factory whose
/// <see cref="DriverType"/> matches <see cref="DeviceConfig.Driver"/>. This is the seam that
/// later allows drivers to be loaded as plugins.
/// </summary>
public interface IDeviceDriverFactory
{
    /// <summary>Driver type key, matched against <see cref="DeviceConfig.Driver"/> (case-insensitive).</summary>
    string DriverType { get; }

    IDeviceDriver Create(DeviceConfig config);

    /// <summary>
    /// Check a configuration <em>before</em> anything is created, returning one message per problem
    /// (empty = usable). This is what lets the engine refuse a single bad connection while keeping
    /// everything else running: without it, a driver constructor throwing on, say, an unparseable
    /// address would abort startup or leave a reload half-applied.
    ///
    /// Report every problem found rather than the first, so an engineer fixes one round of errors.
    /// The default accepts anything, so a driver only implements this when it can say something useful.
    /// </summary>
    IReadOnlyList<string> Validate(DeviceConfig config) => Array.Empty<string>();
}
