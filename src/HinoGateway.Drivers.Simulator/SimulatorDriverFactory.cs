using HinoGateway.Core;

namespace HinoGateway.Drivers.Simulator;

public sealed class SimulatorDriverFactory : IDeviceDriverFactory
{
    public string DriverType => "Simulator";

    public IDeviceDriver Create(DeviceConfig config) => new SimulatorDriver(config);
}
