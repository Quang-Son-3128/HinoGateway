using HinoGateway.Core;

namespace HinoGateway.Server;

/// <summary>Maps between the UI/REST <see cref="DeviceDto"/> and the domain <see cref="DeviceConfig"/>.</summary>
public static class ConfigMapping
{
    public static DeviceDto ToDto(DeviceConfig d) => new()
    {
        Id = d.DeviceId,
        Driver = d.Driver,
        ScanIntervalMs = (int)d.ScanInterval.TotalMilliseconds,
        Settings = new Dictionary<string, string>(d.Settings),
        // Read-only convenience: the tags currently referencing this connection (path = runtime id).
        Tags = d.Tags.Select(t => new TagDto
        {
            DeviceId = d.DeviceId,
            Name = t.Name,
            Path = t.Id,
            Address = t.Address,
            DataType = t.DataType,
            Unit = t.Unit,
            Description = t.Description,
            Scale = t.Scale,
            Offset = t.Offset,
        }).ToList(),
    };

    /// <summary>Connection fields only — tags are managed via the tag endpoints.</summary>
    public static DeviceConfig ToConfig(DeviceDto d) => new()
    {
        DeviceId = d.Id.Trim(),
        Driver = d.Driver,
        ScanInterval = TimeSpan.FromMilliseconds(d.ScanIntervalMs <= 0 ? 1000 : d.ScanIntervalMs),
        Settings = new Dictionary<string, string>(d.Settings),
    };
}
