namespace HinoGateway.Core;

/// <summary>
/// The configuration change took effect in the running gateway but could <b>not</b> be written to
/// disk.
///
/// <para>
/// It has its own type because the two halves of that sentence need different handling: the engine
/// keeps polling with the new configuration, while the request that caused it must still fail. A
/// success response here would tell the operator their change is durable when it will vanish at the
/// next restart — and they would only find out weeks later, after a power cut, with the plant
/// running on a configuration nobody remembers editing.
/// </para>
///
/// <para>⚠ Reconstructed 2026-08-10 after the source tree was lost; shape taken from its call sites
/// in <c>JsonProjectStore</c>, <c>JsonSecretStore</c> and their tests.</para>
/// </summary>
public sealed class ConfigurationPersistenceException : Exception
{
    public ConfigurationPersistenceException(string path, string message, Exception? inner = null)
        : base(message, inner) => Path = path;

    /// <summary>Absolute path of the file that could not be written.</summary>
    public string Path { get; }
}
