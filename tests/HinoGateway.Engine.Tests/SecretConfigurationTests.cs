using HinoGateway.Core;
using HinoGateway.Server;

namespace HinoGateway.Engine.Tests;

public sealed class SecretConfigurationTests
{
    [Fact]
    public void Mqtt_literal_is_replaced_by_a_reference_and_resolved_only_in_the_runtime_copy()
    {
        var secrets = new MemorySecrets();
        var stored = new MqttPublisherOptions { Broker = "broker", Password = "very-secret" };

        Assert.True(SecretConfiguration.ProtectMqtt(stored, secrets, clearEmpty: true));
        Assert.Equal("${secret:mqtt.Password}", stored.Password);
        Assert.True(secrets.TryGet("mqtt.Password", out var secret));
        Assert.Equal("very-secret", secret);

        Assert.True(SecretConfiguration.TryCreateMqttRuntime(stored, secrets, out var runtime, out var error));
        Assert.Null(error);
        Assert.Equal("very-secret", runtime.Password);
        Assert.Equal("${secret:mqtt.Password}", stored.Password);
    }

    [Fact]
    public void Missing_mqtt_secret_is_reported_instead_of_using_the_reference_as_a_password()
    {
        var stored = new MqttPublisherOptions { Password = "${secret:mqtt.Password}" };

        Assert.False(SecretConfiguration.TryCreateMqttRuntime(
            stored, new MemorySecrets(), out var runtime, out var error));

        Assert.Null(runtime.Password);
        Assert.Contains("cannot be resolved", error);
    }

    [Fact]
    public void Driver_secret_settings_are_moved_to_a_per_device_name()
    {
        var secrets = new MemorySecrets();
        var device = new DeviceConfig
        {
            DeviceId = "PLC-1",
            Driver = "SecretDriver",
            Settings = new Dictionary<string, string> { ["Host"] = "plc", ["Password"] = "pw" },
        };

        var protectedDevice = SecretConfiguration.ProtectDevice(
            device, new SecretFactory(), secrets, clearEmpty: true, out var changed);

        Assert.True(changed);
        Assert.Equal("plc", protectedDevice.Settings["Host"]);
        Assert.Equal("${secret:device.PLC-1.Password}", protectedDevice.Settings["Password"]);
        Assert.True(secrets.TryGet("device.PLC-1.Password", out var password));
        Assert.Equal("pw", password);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public bool TryGet(string name, out string value) => _values.TryGetValue(name, out value!);
        public void Set(string name, string value) => _values[name] = value;
        public bool Delete(string name) => _values.Remove(name);
        public IReadOnlyCollection<string> Names => _values.Keys.ToList();
    }

    private sealed class SecretFactory : IDeviceDriverFactory
    {
        public string DriverType => "SecretDriver";
        public IDeviceDriver Create(DeviceConfig config) => throw new NotSupportedException();
        public DriverDescriptor Describe() => new()
        {
            DriverType = DriverType,
            Settings =
            [
                new DriverSetting { Key = "Host" },
                new DriverSetting { Key = "Password", IsSecret = true },
            ],
        };
    }
}
