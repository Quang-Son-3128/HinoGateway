namespace HinoGateway.Core;

/// <summary>
/// Where the real value of a secret setting lives.
///
/// <para>
/// ⚠ <b>Reconstructed on 2026-08-10</b> after the source tree was lost, from the way the rest of the
/// code uses it (<c>DeviceEngine</c>, <c>ProjectValidator</c>, <c>MqttSecrets</c>,
/// <c>JsonSecretStore</c>). The behaviour below is what those call sites require; a helper that
/// existed and was used nowhere else may be missing.
/// </para>
/// </summary>
public enum SecretSource
{
    /// <summary>The gateway's own secret file (<c>hino.secrets.json</c>).</summary>
    Store = 0,

    /// <summary>An environment variable — for platforms that already manage credentials.</summary>
    Environment = 1,
}

/// <summary>
/// A reference to a secret rather than the secret itself: <c>${secret:name}</c> or <c>${env:NAME}</c>.
///
/// <para>
/// This indirection is the whole point of the design. A project file is meant to be copied, diffed,
/// exported and mailed; the moment a password is stored inside it, the password travels with every
/// one of those copies. Settings therefore hold a reference, and the real value is fetched from the
/// secret store on the machine that actually runs the gateway.
/// </para>
/// </summary>
public sealed record SecretRef(SecretSource Source, string Name)
{
    /// <summary>Reads <c>${secret:name}</c> / <c>${env:NAME}</c>; false for a literal value.</summary>
    public static bool TryParse(string? value, out SecretRef reference)
    {
        reference = null!;
        if (value is null)
            return false;

        var text = value.Trim();
        if (text.Length < 4 || !text.StartsWith("${", StringComparison.Ordinal) || !text.EndsWith("}", StringComparison.Ordinal))
            return false;

        var inner = text[2..^1];
        var separator = inner.IndexOf(':');
        if (separator <= 0 || separator == inner.Length - 1)
            return false;

        var scheme = inner[..separator].Trim();
        var name = inner[(separator + 1)..].Trim();
        if (name.Length == 0)
            return false;

        if (string.Equals(scheme, "secret", StringComparison.OrdinalIgnoreCase))
        {
            reference = new SecretRef(SecretSource.Store, name);
            return true;
        }
        if (string.Equals(scheme, "env", StringComparison.OrdinalIgnoreCase))
        {
            reference = new SecretRef(SecretSource.Environment, name);
            return true;
        }
        return false;
    }

    /// <summary>True when the value is a reference rather than a literal secret.</summary>
    public static bool IsReference(string? value) => TryParse(value, out _);

    /// <summary>The name a connection's secret is stored under, e.g. <c>device.MB-1.Password</c>.</summary>
    public static string NameFor(string deviceId, string settingKey) => $"device.{deviceId}.{settingKey}";

    public override string ToString() =>
        Source == SecretSource.Environment ? $"${{env:{Name}}}" : $"${{secret:{Name}}}";
}

/// <summary>
/// Holds the actual secret values, outside the project file. Implemented by
/// <c>JsonSecretStore</c>; kept as an interface here so the engine depends only on the abstraction.
/// </summary>
public interface ISecretStore
{
    bool TryGet(string name, out string value);
    void Set(string name, string value);
    bool Delete(string name);
    IReadOnlyCollection<string> Names { get; }
}

/// <summary>Turns the references in a settings dictionary into the values a driver can use.</summary>
public static class Secrets
{
    /// <summary>
    /// Replace every <c>${secret:...}</c> / <c>${env:...}</c> value with what it points at.
    ///
    /// <para>
    /// Returns the <em>same instance</em> when there was nothing to resolve, so the common case
    /// allocates nothing and the caller can tell by reference whether anything changed. A reference
    /// that cannot be resolved is reported through <paramref name="onProblem"/> and left empty
    /// rather than passed through as the literal <c>${secret:…}</c> text — a driver would otherwise
    /// try to authenticate with the reference itself and the failure would be baffling.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolve(
        IReadOnlyDictionary<string, string> settings,
        ISecretStore? store,
        Action<string>? onProblem = null)
    {
        Dictionary<string, string>? resolved = null;

        foreach (var (key, value) in settings)
        {
            if (!SecretRef.TryParse(value, out var reference))
                continue;

            string? actual = null;
            switch (reference.Source)
            {
                case SecretSource.Environment:
                    actual = System.Environment.GetEnvironmentVariable(reference.Name);
                    if (string.IsNullOrEmpty(actual))
                        onProblem?.Invoke($"setting '{key}' refers to environment variable '{reference.Name}', which is not set");
                    break;

                default:
                    if (store is null)
                        onProblem?.Invoke($"setting '{key}' refers to secret '{reference.Name}', but no secret store is configured");
                    else if (store.TryGet(reference.Name, out var stored))
                        actual = stored;
                    else
                        onProblem?.Invoke($"setting '{key}' refers to secret '{reference.Name}', which is not stored on this machine");
                    break;
            }

            resolved ??= new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase);
            resolved[key] = actual ?? "";
        }

        return resolved ?? settings;
    }
}
