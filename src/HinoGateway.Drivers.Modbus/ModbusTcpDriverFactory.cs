using HinoGateway.Core;

namespace HinoGateway.Drivers.Modbus;

public sealed class ModbusTcpDriverFactory : IDeviceDriverFactory
{
    public string DriverType => "ModbusTCP";
    public IDeviceDriver Create(DeviceConfig config) => new ModbusTcpDriver(config);

    public IReadOnlyList<string> Validate(DeviceConfig config)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Setting(config, "Host")))
            problems.Add("Setting 'Host' is required (the device's IP address or hostname).");
        Number("Port", 1, 65535);
        Number("UnitId", 1, 255);
        Number("TimeoutMs", 50, 10_000);
        Number("Retries", 0, 10);
        Number("MaxRegisterGap", 0, ModbusReadPlan.MaxRegistersPerRead);
        Number("MaxBitGap", 0, ModbusReadPlan.MaxBitsPerRead);
        Number("PollDeadlineMs", 100, 600_000);
        Number("QuarantineMs", 1_000, 86_400_000);
        foreach (var key in new[] { "WordSwap", "ByteSwap" })
            if (Setting(config, key) is { Length: > 0 } text && !bool.TryParse(text, out _))
                problems.Add($"Setting '{key}' must be true or false (got '{text}').");

        // Checked as a pair rather than one at a time: each is individually reasonable, and it is
        // their product that decides whether a reload can stop this connection. Only meaningful once
        // both parsed, or the message would quote a default the operator never chose.
        if (Int("TimeoutMs", 1000) is { } timeout && Int("Retries", 1) is { } retries
            && DriverLimits.CheckCancellationBudget("TimeoutMs", timeout, "Retries", retries) is { } budget)
            problems.Add(budget);

        return problems;

        int? Int(string key, int dflt)
        {
            var text = Setting(config, key);
            if (text.Length == 0) return dflt;
            return int.TryParse(text, out var value) ? value : null;
        }

        void Number(string key, int min, int max)
        {
            var text = Setting(config, key);
            if (text.Length > 0 && (!int.TryParse(text, out var value) || value < min || value > max))
                problems.Add($"Setting '{key}' must be a number between {min} and {max} (got '{text}').");
        }
    }

    public IReadOnlyDictionary<string, string> ValidateTags(DeviceConfig config)
    {
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in config.Tags)
        {
            if (tag.DataType == DataType.String)
            {
                problems[tag.Id] = $"Tag '{tag.Name}': the Modbus driver does not support String tags yet.";
                continue;
            }
            try
            {
                var address = ModbusAddress.Parse(tag.Address);
                var registers = ModbusAddress.RegisterCount(tag.DataType);
                if (address.Type is ModbusRegisterType.HoldingRegister or ModbusRegisterType.InputRegister
                    && address.Address + registers > ushort.MaxValue + 1)
                    problems[tag.Id] = $"Tag '{tag.Name}': a {tag.DataType} needs {registers} registers and exceeds the address space.";
                else if (address.Type is ModbusRegisterType.Coil or ModbusRegisterType.DiscreteInput
                         && tag.DataType != DataType.Bool)
                    problems[tag.Id] = $"Tag '{tag.Name}': a bit area must use Bool, not {tag.DataType}.";
                else if (address.Bit is not null && tag.DataType != DataType.Bool)
                    problems[tag.Id] = $"Tag '{tag.Name}': a register bit address must use Bool, not {tag.DataType}.";
            }
            catch (FormatException ex) { problems[tag.Id] = $"Tag '{tag.Name}', address '{tag.Address}': {ex.Message}"; }
        }
        return problems;
    }

    public DriverDescriptor Describe() => new()
    {
        DriverType = DriverType,
        DisplayName = "Modbus TCP",
        Description = "Modbus TCP master with grouped reads, block diagnostics and safe writes.",
        Capabilities = DriverCapabilities.Writable | DriverCapabilities.BulkRead | DriverCapabilities.Diagnostics,
        EndpointFormat = "{Host}:{Port} unit {UnitId}",
        AddressSyntaxHelp = "HR/IR/COIL/DI addresses, e.g. HR:0, IR:20, COIL:10 or 40001. A register bit may end in .0-.15.",
        AddressExamples = ["HR:0", "IR:20", "COIL:10", "40001", "HR:12.3"],
        SupportedTypes = Enum.GetValues<DataType>().Where(t => t != DataType.String).ToArray(),
        Settings =
        [
            new DriverSetting { Key = "Host", Label = "Host", Required = true, Default = "127.0.0.1" },
            new DriverSetting { Key = "Port", Label = "Port", Kind = DriverSettingKind.Number, Default = "502", Min = 1, Max = 65535 },
            new DriverSetting { Key = "UnitId", Label = "Unit ID", Kind = DriverSettingKind.Number, Default = "1", Min = 1, Max = 255 },
            new DriverSetting { Key = "TimeoutMs", Label = "Timeout (ms)", Kind = DriverSettingKind.Number, Default = "1000", Min = 50, Max = 10000 },
            new DriverSetting { Key = "Retries", Label = "Retries", Kind = DriverSettingKind.Number, Default = "1", Min = 0, Max = 10 },
            new DriverSetting { Key = "WordSwap", Label = "Swap words", Kind = DriverSettingKind.Bool, Default = "false" },
            new DriverSetting { Key = "ByteSwap", Label = "Swap bytes", Kind = DriverSettingKind.Bool, Default = "false" },
            new DriverSetting { Key = "MaxRegisterGap", Label = "Max register gap", Kind = DriverSettingKind.Number, Default = "8", Min = 0, Max = 125, Advanced = true },
            new DriverSetting { Key = "MaxBitGap", Label = "Max bit gap", Kind = DriverSettingKind.Number, Default = "64", Min = 0, Max = 2000, Advanced = true },
            new DriverSetting { Key = "PollDeadlineMs", Label = "Poll deadline (ms)", Kind = DriverSettingKind.Number, Default = "5000", Min = 100, Max = 600000, Advanced = true },
            new DriverSetting { Key = "QuarantineMs", Label = "Quarantine (ms)", Kind = DriverSettingKind.Number, Default = "60000", Min = 1000, Max = 86400000, Advanced = true },
        ],
    };

    private static string Setting(DeviceConfig config, string key) =>
        config.Settings.TryGetValue(key, out var value) ? value?.Trim() ?? "" : "";
}
