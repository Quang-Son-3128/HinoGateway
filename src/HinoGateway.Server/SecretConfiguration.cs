using HinoGateway.Core;

namespace HinoGateway.Server;

/// <summary>
/// Moves literal credentials out of portable project configuration and resolves them only for the
/// runtime component that needs them. Kept at the host boundary: drivers and northbound publishers
/// should never know where credentials are stored.
/// </summary>
internal static class SecretConfiguration
{
    public const string MqttPasswordName = "mqtt.Password";

    public static string MqttPasswordReference =>
        new SecretRef(SecretSource.Store, MqttPasswordName).ToString();

    public static DeviceConfig ProtectDevice(DeviceConfig device, IDeviceDriverFactory factory,
        ISecretStore secrets, bool clearEmpty, out bool changed)
    {
        changed = false;
        var settings = new Dictionary<string, string>(device.Settings, StringComparer.OrdinalIgnoreCase);
        foreach (var setting in factory.Describe().Settings.Where(s => s.IsSecret))
        {
            if (!settings.TryGetValue(setting.Key, out var value))
                continue;

            var name = SecretRef.NameFor(device.DeviceId, setting.Key);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (clearEmpty)
                {
                    settings.Remove(setting.Key);
                    secrets.Delete(name);
                    changed = true;
                }
                continue;
            }
            if (SecretRef.IsReference(value))
                continue;

            secrets.Set(name, value);
            settings[setting.Key] = new SecretRef(SecretSource.Store, name).ToString();
            changed = true;
        }
        return changed ? device with { Settings = settings } : device;
    }

    public static bool ProtectMqtt(MqttPublisherOptions options, ISecretStore secrets, bool clearEmpty)
    {
        if (string.IsNullOrWhiteSpace(options.Password))
        {
            if (clearEmpty)
                secrets.Delete(MqttPasswordName);
            options.Password = null;
            return clearEmpty;
        }
        if (SecretRef.IsReference(options.Password))
            return false;

        secrets.Set(MqttPasswordName, options.Password);
        options.Password = MqttPasswordReference;
        return true;
    }

    public static bool TryCreateMqttRuntime(MqttPublisherOptions stored, ISecretStore secrets,
        out MqttPublisherOptions runtime, out string? error)
    {
        error = null;
        string? password = stored.Password;
        if (SecretRef.TryParse(stored.Password, out var reference))
        {
            password = reference.Source switch
            {
                SecretSource.Environment => Environment.GetEnvironmentVariable(reference.Name),
                SecretSource.Store when secrets.TryGet(reference.Name, out var value) => value,
                _ => null,
            };
            if (password is null)
                error = $"MQTT password reference '{reference}' cannot be resolved on this machine.";
        }

        runtime = new MqttPublisherOptions
        {
            Enabled = stored.Enabled,
            Broker = stored.Broker,
            Port = stored.Port,
            UseTls = stored.UseTls,
            ClientId = stored.ClientId,
            Username = stored.Username,
            Password = password,
            PublishTopic = stored.PublishTopic,
            QoS = stored.QoS,
            Retain = stored.Retain,
            BatchIntervalMs = stored.BatchIntervalMs,
            QueueCapacity = stored.QueueCapacity,
            MaxSpoolBytes = stored.MaxSpoolBytes,
            ReconnectMinMs = stored.ReconnectMinMs,
            ReconnectMaxMs = stored.ReconnectMaxMs,
        };
        return error is null;
    }
}
