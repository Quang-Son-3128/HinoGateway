using System.Text.Json;
using HinoGateway.Core;
using HinoGateway.Drivers.Modbus;
using HinoGateway.Drivers.SiemensS7;
using HinoGateway.Drivers.Simulator;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// The connection editor renders its whole form from the descriptor the server sends, so the
/// descriptor has to survive the wire intact — a settings list that arrives empty leaves the
/// operator typing Rack and Slot by hand, which is the bug these tests exist to prevent.
/// </summary>
public sealed class DriverDescriptorContractTests
{
    // What Blazor's HttpClient JSON helpers use on the browser side.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_descriptor_survives_the_round_trip_the_editor_makes()
    {
        var original = new SiemensS7DriverFactory().Describe();

        var round = JsonSerializer.Deserialize<DriverDescriptor>(
            JsonSerializer.Serialize(original, Web), Web)!;

        Assert.Equal(original.DriverType, round.DriverType);
        Assert.Equal(original.DisplayName, round.DisplayName);
        Assert.Equal(original.EndpointFormat, round.EndpointFormat);
        Assert.Equal(original.Settings.Count, round.Settings.Count);
        Assert.Equal(original.SupportedTypes, round.SupportedTypes);
        Assert.Equal(original.AddressExamples, round.AddressExamples);

        var slot = round.Setting("Slot")!;
        Assert.Equal(DriverSettingKind.Number, slot.Kind);
        Assert.Equal("1", slot.Default);
        Assert.Equal(0, slot.Min);
        Assert.Equal(255, slot.Max);

        var cpu = round.Setting("CpuType")!;
        Assert.Equal(DriverSettingKind.Choice, cpu.Kind);
        Assert.Contains("S71500", cpu.Choices);
    }

    [Theory]
    [InlineData(typeof(SiemensS7DriverFactory))]
    [InlineData(typeof(ModbusTcpDriverFactory))]
    [InlineData(typeof(SimulatorDriverFactory))]
    public void Every_driver_describes_the_settings_its_own_validator_demands(Type factoryType)
    {
        var factory = (IDeviceDriverFactory)Activator.CreateInstance(factoryType)!;
        var descriptor = factory.SafeDescribe();

        Assert.Equal(factory.DriverType, descriptor.DriverType);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.DisplayName));

        // A form built from the defaults alone must pass the driver's own validation, or "new
        // connection" would open pre-filled with something the gateway then refuses.
        var settings = descriptor.Settings
            .Where(s => !s.Advanced && !s.IsSecret && !string.IsNullOrWhiteSpace(s.Default))
            .ToDictionary(s => s.Key, s => s.Default!, StringComparer.OrdinalIgnoreCase);

        var config = new DeviceConfig
        {
            DeviceId = "D1",
            Driver = descriptor.DriverType,
            Settings = settings,
        };

        Assert.Empty(factory.Validate(config));
    }

    [Fact]
    public void An_endpoint_is_rendered_from_the_drivers_own_template()
    {
        var descriptor = new SiemensS7DriverFactory().Describe();

        var endpoint = descriptor.FormatEndpoint(new Dictionary<string, string>
        {
            ["Host"] = "192.168.10.5",
            ["Rack"] = "0",
            ["Slot"] = "2",
        });

        // Port is absent, so the descriptor's own default fills the gap rather than showing "?".
        Assert.Equal("192.168.10.5:102 R0/S2", endpoint);
    }

    [Fact]
    public void A_driver_that_throws_while_describing_itself_still_appears()
    {
        var descriptor = new BrokenFactory().SafeDescribe();

        Assert.Equal("Broken", descriptor.DriverType);
        Assert.Empty(descriptor.Settings);
    }

    private sealed class BrokenFactory : IDeviceDriverFactory
    {
        public string DriverType => "Broken";
        public IDeviceDriver Create(DeviceConfig config) => throw new NotSupportedException();
        public DriverDescriptor Describe() => throw new InvalidOperationException("boom");
    }
}
