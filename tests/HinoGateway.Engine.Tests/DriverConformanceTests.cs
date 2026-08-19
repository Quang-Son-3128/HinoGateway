using HinoGateway.Core;
using HinoGateway.Drivers.Modbus;
using HinoGateway.Drivers.SiemensS7;
using HinoGateway.Drivers.Simulator;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// What every driver must do, applied to every driver.
///
/// <para>
/// The existing descriptor tests name Siemens and Modbus one at a time, so a fourth driver — or one
/// loaded from the <c>drivers/</c> folder — inherits none of them. These are written against the
/// interface instead: add a factory to <see cref="Factories"/> and it is held to the same rules.
/// That is the half of a plugin contract worth having, and it costs one line per driver.
/// </para>
///
/// <para>
/// Each rule below is here because breaking it produces a specific field failure, named in the test.
/// A conformance suite of things that merely seem tidy would be deleted the first time it was
/// inconvenient.
/// </para>
/// </summary>
public class DriverConformanceTests
{
    public static TheoryData<string> Factories =>
        new() { "Simulator", "ModbusTCP", "SiemensS7" };

    private static IDeviceDriverFactory Create(string driverType) => driverType switch
    {
        "Simulator" => new SimulatorDriverFactory(),
        "ModbusTCP" => new ModbusTcpDriverFactory(),
        "SiemensS7" => new SiemensS7DriverFactory(),
        _ => throw new ArgumentOutOfRangeException(nameof(driverType)),
    };

    [Theory]
    [MemberData(nameof(Factories))]
    public void The_descriptor_names_the_same_driver_the_factory_does(string driverType)
    {
        // The engine matches a device's configured driver against DriverType; the editor renders from
        // the descriptor. If those two disagree the operator configures a connection the engine will
        // then reject as an unknown driver.
        var factory = Create(driverType);
        Assert.Equal(factory.DriverType, factory.Describe().DriverType);
        Assert.False(string.IsNullOrWhiteSpace(factory.Describe().DisplayName));
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void Every_declared_setting_is_usable_as_declared(string driverType)
    {
        foreach (var setting in Create(driverType).Describe().Settings)
        {
            Assert.False(string.IsNullOrWhiteSpace(setting.Key), $"{driverType} has a setting with no key");

            if (setting is { Min: { } min, Max: { } max })
                Assert.True(min <= max, $"{driverType}.{setting.Key} declares Min {min} above Max {max}");

            // A default the driver would itself reject is worse than no default: the editor offers it,
            // the operator accepts it, and the save fails on a value they never chose.
            if (setting is { Default: { Length: > 0 } text, Kind: DriverSettingKind.Number }
                && double.TryParse(text, out var value))
            {
                if (setting.Min is { } lower)
                    Assert.True(value >= lower, $"{driverType}.{setting.Key} defaults to {value}, below its own Min {lower}");
                if (setting.Max is { } upper)
                    Assert.True(value <= upper, $"{driverType}.{setting.Key} defaults to {value}, above its own Max {upper}");
            }

            if (setting.Kind == DriverSettingKind.Choice)
                Assert.True(setting.Choices.Count > 0, $"{driverType}.{setting.Key} is a choice with nothing to choose");
            if (setting is { Kind: DriverSettingKind.Choice, Default.Length: > 0 })
                Assert.Contains(setting.Default, setting.Choices);
        }
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void A_configuration_built_from_the_declared_defaults_is_accepted(string driverType)
    {
        // The path an operator takes when adding a connection and changing nothing but the host. If
        // the driver's own defaults do not validate, the editor's "add" button produces an error the
        // operator has no way to interpret.
        var factory = Create(driverType);
        var descriptor = factory.Describe();
        var settings = descriptor.Settings
            .Where(s => !string.IsNullOrEmpty(s.Default) || s.Required)
            .ToDictionary(s => s.Key, s => string.IsNullOrEmpty(s.Default) ? "127.0.0.1" : s.Default!,
                StringComparer.Ordinal);

        var problems = factory.Validate(new DeviceConfig
        {
            DeviceId = "conformance",
            Driver = factory.DriverType,
            ScanInterval = TimeSpan.FromSeconds(1),
            Settings = settings,
            Tags = [],
        });

        Assert.True(problems.Count == 0,
            $"{driverType} rejects its own declared defaults: {string.Join(" | ", problems)}");
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void Validation_reports_problems_rather_than_throwing(string driverType)
    {
        // The engine calls Validate to decide whether to reject one connection or keep the rest
        // running. A factory that throws instead of reporting takes the whole reload with it.
        var factory = Create(driverType);
        var nonsense = new DeviceConfig
        {
            DeviceId = "conformance",
            Driver = factory.DriverType,
            ScanInterval = TimeSpan.FromSeconds(1),
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Host"] = "",
                ["Port"] = "not-a-number",
                ["TimeoutMs"] = "-1",
            },
            Tags = [new TagDefinition { Id = "a/b", Name = "b", Address = "!!not-an-address!!" }],
        };

        var problems = factory.Validate(nonsense);
        var tagProblems = factory.ValidateTags(nonsense);

        Assert.NotNull(problems);
        Assert.NotNull(tagProblems);
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void A_driver_can_be_created_and_disposed_without_ever_connecting(string driverType)
    {
        // Reload builds every driver before starting any of them, and a rejected connection is
        // disposed without a Connect. A constructor that reaches the network, or a Dispose that
        // assumes one happened, turns a configuration change into a hang.
        var factory = Create(driverType);
        var descriptor = factory.Describe();
        var settings = descriptor.Settings
            .Where(s => !string.IsNullOrEmpty(s.Default) || s.Required)
            .ToDictionary(s => s.Key, s => string.IsNullOrEmpty(s.Default) ? "127.0.0.1" : s.Default!,
                StringComparer.Ordinal);

        var driver = factory.Create(new DeviceConfig
        {
            DeviceId = "conformance",
            Driver = factory.DriverType,
            ScanInterval = TimeSpan.FromSeconds(1),
            Settings = settings,
            Tags = [],
        });

        var disposed = driver.DisposeAsync();
        Assert.True(disposed.IsCompleted, $"{driverType}.DisposeAsync blocked on a driver that never connected");
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public async Task Aborting_a_driver_that_never_connected_is_harmless(string driverType)
    {
        // AbortAsync is called on the shutdown path, possibly before Connect ever ran and possibly
        // while a poll is in flight. It must never throw: the engine has nowhere to report it and a
        // failure here is what leaves an abandoned worker behind.
        var factory = Create(driverType);
        var descriptor = factory.Describe();
        var settings = descriptor.Settings
            .Where(s => !string.IsNullOrEmpty(s.Default) || s.Required)
            .ToDictionary(s => s.Key, s => string.IsNullOrEmpty(s.Default) ? "127.0.0.1" : s.Default!,
                StringComparer.Ordinal);

        await using var driver = factory.Create(new DeviceConfig
        {
            DeviceId = "conformance",
            Driver = factory.DriverType,
            ScanInterval = TimeSpan.FromSeconds(1),
            Settings = settings,
            Tags = [],
        });

        await driver.AbortAsync();
        await driver.AbortAsync();   // twice, because shutdown and reload can both reach it
    }
}
