using HinoGateway.Core;

namespace HinoGateway.Server;

/// <summary>
/// Demo configuration seeded into an empty config store on first run: two simulated connections
/// and a small tag tree (Simulation → Area 1 / Area 2) so the folder/connection separation is
/// visible out of the box.
/// </summary>
public static class DemoConfiguration
{
    public static IReadOnlyList<DeviceConfig> Devices { get; } =
    [
        new DeviceConfig { DeviceId = "SIM-1", Driver = "Simulator", ScanInterval = TimeSpan.FromMilliseconds(500) },
        new DeviceConfig { DeviceId = "SIM-2", Driver = "Simulator", ScanInterval = TimeSpan.FromSeconds(1) },
    ];

    public static IReadOnlyList<TagGroupDto> Groups { get; } =
    [
        new TagGroupDto { Id = "demo-sim",   ParentId = null,       Name = "Simulation" },
        new TagGroupDto { Id = "demo-area1", ParentId = "demo-sim", Name = "Area 1" },
        new TagGroupDto { Id = "demo-area2", ParentId = "demo-sim", Name = "Area 2" },
    ];

    public static IReadOnlyList<TagDto> Tags { get; } =
    [
        new TagDto { Id = "demo-sine",   GroupId = "demo-area1", DeviceId = "SIM-1", Name = "Sine",     Address = "sine",   DataType = DataType.Float64, Unit = "%" },
        new TagDto { Id = "demo-ramp",   GroupId = "demo-area1", DeviceId = "SIM-1", Name = "Ramp",     Address = "ramp",   DataType = DataType.Float64, Unit = "%" },
        new TagDto { Id = "demo-random", GroupId = "demo-area1", DeviceId = "SIM-1", Name = "Random",   Address = "random", DataType = DataType.Float64, Unit = "%" },
        new TagDto { Id = "demo-run",    GroupId = "demo-area1", DeviceId = "SIM-1", Name = "Run",      Address = "bool",   DataType = DataType.Bool },
        new TagDto { Id = "demo-temp",   GroupId = "demo-area2", DeviceId = "SIM-2", Name = "Temp",     Address = "sine",   DataType = DataType.Float64, Unit = "°C" },
        new TagDto { Id = "demo-sp",     GroupId = "demo-area2", DeviceId = "SIM-2", Name = "Setpoint", Address = "42.5",   DataType = DataType.Float64, Unit = "°C" },
    ];

    /// <summary>
    /// Example alarms on the simulated tags, so a fresh install shows the alarm lifecycle working
    /// (the sine tag crosses these limits every cycle). TagId is the tag's storage key.
    /// </summary>
    public static IReadOnlyList<AlarmDto> Alarms { get; } =
    [
        new AlarmDto
        {
            Id = "demo-alarm-hi", Name = "Sine high", TagId = "demo-sine",
            Condition = AlarmCondition.Hi, Limit = 80, Hysteresis = 5,
            Severity = AlarmSeverity.Warning, Message = "Sine above 80 %",
        },
        new AlarmDto
        {
            Id = "demo-alarm-hihi", Name = "Sine very high", TagId = "demo-sine",
            Condition = AlarmCondition.HiHi, Limit = 95, Hysteresis = 5, OnDelayMs = 1000,
            Severity = AlarmSeverity.Critical, Message = "Sine above 95 %",
        },
        new AlarmDto
        {
            Id = "demo-alarm-comms", Name = "Temp comms fault", TagId = "demo-temp",
            Condition = AlarmCondition.BadQuality, OnDelayMs = 3000,
            Severity = AlarmSeverity.Major, Message = "No good data from SIM-2",
        },
    ];
}
