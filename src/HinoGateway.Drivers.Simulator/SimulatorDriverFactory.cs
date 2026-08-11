using HinoGateway.Core;

namespace HinoGateway.Drivers.Simulator;

public sealed class SimulatorDriverFactory : IDeviceDriverFactory
{
    public string DriverType => "Simulator";

    public IDeviceDriver Create(DeviceConfig config) => new SimulatorDriver(config);

    public DriverDescriptor Describe() => new()
    {
        DriverType = DriverType,
        DisplayName = "Simulator",
        Description = "Built-in deterministic waveforms for commissioning and load tests.",
        Capabilities = DriverCapabilities.Writable,
        AddressSyntaxHelp = "sine, ramp, random, bool, or a numeric constant.",
        AddressExamples = ["sine", "ramp", "random", "bool", "42.5"],
        SupportedTypes = Enum.GetValues<DataType>(),
    };
}
