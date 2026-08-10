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
    /// Build a driver, handing it the shared link when the engine has one for this connection.
    /// A driver that does not share ignores the argument; the default forwards to
    /// <see cref="Create(DeviceConfig)"/> so an existing driver needs no change.
    /// </summary>
    IDeviceDriver Create(DeviceConfig config, IDeviceTransport? transport) => Create(config);

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

    /// <summary>
    /// Per-tag checks, keyed by tag id. Anything reported here costs <em>that tag</em> and nothing
    /// else: the connection keeps polling the rest and the tag is shown Bad with the reason.
    ///
    /// <para>
    /// Address and data-type checks belong here, not in <see cref="Validate"/>. When they lived
    /// there, one mistyped address rejected the whole connection and blinded every other tag behind
    /// it — which is how a typo in one register turned into a plant with no data.
    /// </para>
    /// </summary>
    IReadOnlyDictionary<string, string> ValidateTags(DeviceConfig config) =>
        new Dictionary<string, string>();

    /// <summary>
    /// Checks that only make sense across all the connections of this driver at once — two RS-485
    /// connections naming the same port but disagreeing about its baud rate, for example. A conflict
    /// over a shared link belongs to no single connection, so validating them one at a time cannot
    /// see it. Empty by default.
    /// </summary>
    IReadOnlyList<string> ValidateTogether(IReadOnlyList<DeviceConfig> configs) => Array.Empty<string>();

    /// <summary>
    /// The physical link this connection uses, or null when it owns its link. Two connections that
    /// return the same key get the same <see cref="IDeviceTransport"/> instance and their requests
    /// are serialised on it. The engine namespaces the key by driver type, so two protocols can
    /// never collide on the same string.
    /// </summary>
    string? TransportKey(DeviceConfig config) => null;

    /// <summary>Create the shared link named by <see cref="TransportKey"/>. Called once per key.</summary>
    IDeviceTransport CreateTransport(string key, DeviceConfig config) =>
        throw new NotSupportedException($"Driver '{DriverType}' does not support shared links.");

    /// <summary>
    /// How the driver describes itself to the configuration UI — settings with defaults and bounds,
    /// address syntax, supported types. The connection editor renders its whole form from this, so a
    /// new driver needs no client change.
    /// </summary>
    DriverDescriptor Describe() => DriverDescriptor.Minimal(DriverType);
}
