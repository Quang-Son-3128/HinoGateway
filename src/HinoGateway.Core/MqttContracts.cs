namespace HinoGateway.Core;

/// <summary>
/// Configuration of the MQTT northbound publisher.
///
/// <para>
/// ⚠ <b>Reconstructed on 2026-08-10</b> after the source tree was lost, then <b>verified field by
/// field</b> against a real <c>.hino</c> export taken at 09:49 that day
/// (<c>hino-backup-20260810.hino</c> in the repo root). The property set below is exactly the one
/// that export contains, so an old configuration round-trips. What cannot be recovered from an
/// export is a member that never reaches JSON — a computed property or a helper method.
/// </para>
/// </summary>
public sealed class MqttPublisherOptions
{
    /// <summary>Off by default: a gateway only publishes upstream once someone has configured where to.</summary>
    public bool Enabled { get; set; }

    public string Broker { get; set; } = "";

    public int Port { get; set; } = 1883;

    /// <summary>
    /// TLS on the broker connection. Note the trap this project has already been bitten by: leaving
    /// this on while pointing at a plaintext listener (1883) fails inside ConnectAsync with a socket
    /// reset, which looks like a network problem rather than a configuration one.
    /// </summary>
    public bool UseTls { get; set; }

    public string ClientId { get; set; } = "hinogateway";

    public string? Username { get; set; }

    /// <summary>
    /// Stored as a <c>${secret:mqtt.Password}</c> reference, never as the literal value — see
    /// <c>MqttSecrets</c>. The real value lives in the secret store beside the project file.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>Topic the batches are published to. MQTT wildcards are refused by validation.</summary>
    public string PublishTopic { get; set; } = "hinogateway/telemetry";

    /// <summary>0, 1 or 2.</summary>
    public int QoS { get; set; } = 1;

    public bool Retain { get; set; }

    /// <summary>
    /// How often a batch is published — and therefore also the disk write rate, because every batch
    /// is written to the spool before it is sent so that delivery survives a power cut. 5 s is the
    /// shipped default: the old 500 ms cost ten times the flash wear on the card an IPC boots from.
    /// </summary>
    public int BatchIntervalMs { get; set; } = 5000;

    /// <summary>Batches held in memory before the oldest are spooled to disk.</summary>
    public int QueueCapacity { get; set; } = 8192;

    /// <summary>
    /// Ceiling on the on-disk spool (1 MB … 10 GB). The spool itself lives in
    /// <c>data/mqtt-spool</c> beside the executable — a path the publisher owns, not a setting.
    /// </summary>
    public long MaxSpoolBytes { get; set; } = 268435456;

    public int ReconnectMinMs { get; set; } = 1000;

    public int ReconnectMaxMs { get; set; } = 30000;
}

/// <summary>One tag reading as it travels northbound over MQTT.</summary>
public sealed record MqttTagSample(
    string TagId,
    object? Value,
    Quality Quality,
    DateTimeOffset SourceTimestamp,
    string? StatusText);

/// <summary>
/// A batch of readings — the unit that is queued, spooled and published. Batching rather than
/// publishing per tag is what keeps the message count (and the flash writes behind it) proportional
/// to time instead of to the number of tags.
/// </summary>
public sealed record MqttBatch(
    string GatewayId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<MqttTagSample> Samples);
