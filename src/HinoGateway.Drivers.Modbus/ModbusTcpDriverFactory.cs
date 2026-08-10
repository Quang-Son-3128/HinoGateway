using HinoGateway.Core;

namespace HinoGateway.Drivers.Modbus;

public sealed class ModbusTcpDriverFactory : IDeviceDriverFactory
{
    public string DriverType => "ModbusTCP";

    public IDeviceDriver Create(DeviceConfig config) => new ModbusTcpDriver(config);

    /// <summary>
    /// Everything the driver's constructor would otherwise throw on, reported up front and per tag
    /// so one mistyped address cannot take the connection — or the gateway's startup — down with it.
    /// </summary>
    public IReadOnlyList<string> Validate(DeviceConfig config)
    {
        var problems = new List<string>();

        var host = Setting(config, "Host");
        if (string.IsNullOrWhiteSpace(host))
            problems.Add("Setting 'Host' is required (the device's IP address or hostname).");

        if (Setting(config, "Port") is { Length: > 0 } port
            && (!int.TryParse(port, out var portNumber) || portNumber is < 1 or > 65535))
            problems.Add($"Setting 'Port' must be a number between 1 and 65535 (got '{port}').");

        if (Setting(config, "UnitId") is { Length: > 0 } unit
            && (!byte.TryParse(unit, out var unitId) || unitId == 0))
            problems.Add($"Setting 'UnitId' must be a number between 1 and 255 (got '{unit}').");

        if (Setting(config, "TimeoutMs") is { Length: > 0 } timeout
            && (!int.TryParse(timeout, out var timeoutMs) || timeoutMs < 50))
            problems.Add($"Setting 'TimeoutMs' must be a number of at least 50 (got '{timeout}').");

        if (Setting(config, "Retries") is { Length: > 0 } retries
            && (!int.TryParse(retries, out var retryCount) || retryCount is < 0 or > 10))
            problems.Add($"Setting 'Retries' must be a number between 0 and 10 (got '{retries}').");

        foreach (var flag in new[] { "WordSwap", "ByteSwap" })
        {
            if (Setting(config, flag) is { Length: > 0 } value && !bool.TryParse(value, out _))
                problems.Add($"Setting '{flag}' must be true or false (got '{value}').");
        }

        foreach (var (key, min, max) in new[]
                 {
                     ("MaxRegisterGap", 0, ModbusReadPlan.MaxRegistersPerRead),
                     ("MaxBitGap", 0, ModbusReadPlan.MaxBitsPerRead),
                     ("PollDeadlineMs", 100, 600_000),
                     ("QuarantineMs", 1_000, 86_400_000),
                 })
        {
            if (Setting(config, key) is { Length: > 0 } value
                && (!int.TryParse(value, out var number) || number < min || number > max))
                problems.Add($"Setting '{key}' must be a number between {min} and {max} (got '{value}').");
        }

        foreach (var tag in config.Tags)
        {
            if (tag.DataType == DataType.String)
            {
                problems.Add($"Tag '{tag.Name}': the Modbus driver cannot read String tags yet — use a numeric type.");
                continue;
            }

            ModbusAddress address;
            try
            {
                address = ModbusAddress.Parse(tag.Address);
            }
            catch (FormatException ex)
            {
                problems.Add($"Tag '{tag.Name}' (address '{tag.Address}'): {ex.Message}");
                continue;
            }

            // A multi-register type must not run off the end of the address space.
            var registers = ModbusAddress.RegisterCount(tag.DataType);
            if (address.Type is ModbusRegisterType.HoldingRegister or ModbusRegisterType.InputRegister
                && address.Address + registers > ushort.MaxValue + 1)
            {
                problems.Add($"Tag '{tag.Name}': a {tag.DataType} needs {registers} registers, " +
                             $"which runs past the end of the address space at {address.Address}.");
            }

            if (address.Type is ModbusRegisterType.Coil or ModbusRegisterType.DiscreteInput
                && tag.DataType != DataType.Bool)
            {
                problems.Add($"Tag '{tag.Name}': address '{tag.Address}' is a single bit, so its type must be Bool " +
                             $"(got {tag.DataType}).");
            }

            if (address.Bit is not null && tag.DataType != DataType.Bool)
            {
                problems.Add($"Tag '{tag.Name}': address '{tag.Address}' selects one bit of a register, " +
                             $"so its type must be Bool (got {tag.DataType}).");
            }
        }

        return problems;
    }

    private static string Setting(DeviceConfig config, string key) =>
        config.Settings.TryGetValue(key, out var value) ? value?.Trim() ?? "" : "";
}
