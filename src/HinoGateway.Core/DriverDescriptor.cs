namespace HinoGateway.Core;

/// <summary>What kind of value a driver setting holds, so the editor can render the right control.</summary>
public enum DriverSettingKind
{
    Text = 0,
    Number = 1,
    Bool = 2,
    Choice = 3,
}

/// <summary>
/// One setting a driver accepts, described well enough that the connection editor can render and
/// validate it without knowing anything about the protocol.
///
/// <para>
/// ⚠ <b>Reconstructed on 2026-08-10</b> after the source tree was lost. Every member below is one
/// that <c>ProjectValidator</c> (which survived verbatim) reads, plus the two the session notes
/// record as hard-won: <see cref="Suggestions"/> and the rule about <see cref="Advanced"/>.
/// </para>
/// </summary>
public sealed record DriverSetting
{
    public required string Key { get; init; }

    /// <summary>Label for the editor; falls back to the key.</summary>
    public string? Label { get; init; }

    /// <summary>One sentence explaining what it does and what to try when it is wrong.</summary>
    public string? Help { get; init; }

    public DriverSettingKind Kind { get; init; } = DriverSettingKind.Text;

    /// <summary>Value used when the operator leaves it blank. Null = no default.</summary>
    public string? Default { get; init; }

    public bool Required { get; init; }

    /// <summary>Numeric bounds, enforced by the validator as well as by the editor.</summary>
    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>
    /// The allowed values for <see cref="DriverSettingKind.Choice"/>. <b>Enforced</b>: anything else
    /// is refused, so only use it where the list really is closed (a Modbus RTU baud rate).
    /// </summary>
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Values offered as a hint but <em>not</em> enforced — the serial ports this machine currently
    /// has, for instance. Deliberately separate from <see cref="Choices"/>: enforcing the list would
    /// block configuring an adapter that is unplugged right now, or preparing a project for another
    /// machine.
    /// </summary>
    public IReadOnlyList<string> Suggestions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// A password or key: never echoed back, never written into the project file as a literal, and
    /// skipped by the validator's value checks so a rejected value cannot end up in a log message.
    /// </summary>
    public bool IsSecret { get; init; }

    /// <summary>
    /// Hidden behind "Add setting" in the editor, which only pre-fills the non-advanced ones.
    ///
    /// <para>
    /// This is for fine-tuning <b>only</b>. Marking something that defines the link itself — a baud
    /// rate, a data-bit count — leaves the operator unable to configure the connection at all, which
    /// is exactly the bug this comment exists to prevent a second time.
    /// </para>
    /// </summary>
    public bool Advanced { get; init; }
}

/// <summary>
/// How a driver describes itself to the configuration UI. The connection editor renders its whole
/// form from this, so adding a driver needs no change in the client.
///
/// <para>⚠ Reconstructed 2026-08-10; shape taken from <c>ProjectValidator</c> and the notes.</para>
/// </summary>
public sealed record DriverDescriptor
{
    public required string DriverType { get; init; }

    public string DisplayName { get; init; } = "";

    /// <summary>One line for the driver picker.</summary>
    public string? Summary { get; init; }

    public IReadOnlyList<DriverSetting> Settings { get; init; } = Array.Empty<DriverSetting>();

    /// <summary>
    /// Data types this driver can decode. Empty means "no opinion" — the validator then checks
    /// nothing, which is what a driver that handles everything wants.
    /// </summary>
    public IReadOnlyCollection<DataType> SupportedTypes { get; init; } = Array.Empty<DataType>();

    /// <summary>Example address, shown as the placeholder in the tag editor.</summary>
    public string? AddressExample { get; init; }

    /// <summary>How to read this driver's address syntax, shown beside the address box.</summary>
    public string? AddressHelp { get; init; }

    public bool SupportsWrite { get; init; } = true;

    /// <summary>Is <paramref name="key"/> a setting this driver knows about? Case-insensitive.</summary>
    public bool Knows(string key) =>
        Settings.Any(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The fallback for a driver that does not describe itself, and for a factory whose
    /// <c>Describe()</c> throws. An empty settings list means the validator checks nothing rather
    /// than refusing every setting, so an undescribed driver still works.
    /// </summary>
    public static DriverDescriptor Minimal(string driverType) => new()
    {
        DriverType = driverType,
        DisplayName = driverType,
    };
}
