namespace HinoGateway.Core;

/// <summary>
/// The portable HinoGateway project file (<c>.hino</c>): a versioned JSON envelope holding the
/// full configuration. Used for backup, sharing, and moving a configuration between
/// installations. Kept as an explicit envelope (not a bare device array) so the format can grow
/// — screens, alarms, users — without breaking older/newer readers.
///
/// Version history:
///  1 — devices each carrying their own tag list (DeviceDto.Tags).
///  2 — connections, tag folders and tags as independent top-level collections.
///  3 — adds alarm definitions.
///  4 — adds the IEC-104 controlled-station configuration.
///  5 — adds the MQTT northbound publisher and disk-spool configuration.
///  6 — adds tag kinds: memory and expression tags alongside the device tags.
/// Older files stay importable: version-1 per-device tags are converted into one folder per
/// device, and collections missing from an older file are simply empty. A version-5 file has no
/// <c>Kind</c> on its tags, which deserialises to <see cref="TagKind.Device"/> — the only kind that
/// existed then.
/// </summary>
public sealed class HinoProject
{
    public const string FormatTag = "hino-project";
    public const int CurrentVersion = 6;

    public string Format { get; set; } = FormatTag;
    public int Version { get; set; } = CurrentVersion;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Note { get; set; }

    public List<DeviceDto> Devices { get; set; } = new();
    public List<TagGroupDto> Groups { get; set; } = new();
    public List<TagDto> Tags { get; set; } = new();

    /// <summary>Since version 3. Each alarm references a tag by its storage key.</summary>
    public List<AlarmDto> Alarms { get; set; } = new();

    /// <summary>Since version 4. Null in a file written before the northbound link existed.</summary>
    public Iec104ServerOptions? Iec104 { get; set; }

    /// <summary>Since version 5. Null in projects created before MQTT northbound support.</summary>
    public MqttPublisherOptions? Mqtt { get; set; }
}
