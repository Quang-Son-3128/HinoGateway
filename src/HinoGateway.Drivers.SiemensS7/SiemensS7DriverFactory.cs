using HinoGateway.Core;
using S7.Net;
using DataType = HinoGateway.Core.DataType;

namespace HinoGateway.Drivers.SiemensS7;

public sealed class SiemensS7DriverFactory : IDeviceDriverFactory
{
    public string DriverType => "SiemensS7";
    public IDeviceDriver Create(DeviceConfig config) => new SiemensS7Driver(config);

    public DriverDescriptor Describe() => new()
    {
        DriverType = DriverType,
        DisplayName = "Siemens S7",
        Description = "S7 communication (ISO-on-TCP, port 102) for S7-300/400/1200/1500 and LOGO!.",
        Capabilities = DriverCapabilities.Writable | DriverCapabilities.BulkRead | DriverCapabilities.Diagnostics,
        EndpointFormat = "{Host}:{Port} R{Rack}/S{Slot}",
        AddressSyntaxHelp =
            "Absolute S7 addresses: DB<n>.DBX/DBB/DBW/DBD<offset> for data blocks, M/MB/MW/MD for merkers, " +
            "I/IB/IW/ID and Q/QB/QW/QD for process image. A bit address ends in .0-.7 and must be Bool.",
        AddressExamples = ["DB1.DBX0.0", "DB1.DBW2", "DB1.DBD4", "M0.0", "MW2", "IW0", "QW0"],
        SupportedTypes = new[]
        {
            DataType.Bool, DataType.Int16, DataType.UInt16, DataType.Int32, DataType.UInt32,
            DataType.Int64, DataType.UInt64, DataType.Float32, DataType.Float64,
        },
        Settings = new[]
        {
            new DriverSetting { Key = "Host", Label = "Host", Default = "192.168.0.1", Required = true, Help = "IP address of the CPU or its communication processor." },
            new DriverSetting { Key = "Port", Label = "Port", Kind = DriverSettingKind.Number, Default = "102", Min = 1, Max = 65535 },
            new DriverSetting { Key = "CpuType", Label = "CPU type", Kind = DriverSettingKind.Choice, Default = "S71200", Choices = ["S7200", "S7300", "S7400", "S71200", "S71500", "Logo0BA8"] },
            new DriverSetting { Key = "Rack", Label = "Rack", Kind = DriverSettingKind.Number, Default = "0", Min = 0, Max = 255 },
            new DriverSetting { Key = "Slot", Label = "Slot", Kind = DriverSettingKind.Number, Default = "1", Min = 0, Max = 255 },
            new DriverSetting { Key = "TimeoutMs", Label = "Timeout (ms)", Kind = DriverSettingKind.Number, Default = "3000", Min = 100, Max = 10_000 },
            new DriverSetting { Key = "MaxGapBytes", Label = "Max gap (bytes)", Kind = DriverSettingKind.Number, Default = "8", Min = 0, Max = 960, Advanced = true },
            new DriverSetting { Key = "MaxBlockBytes", Label = "Max block (bytes)", Kind = DriverSettingKind.Number, Default = "200", Min = 16, Max = 960, Advanced = true },
            new DriverSetting { Key = "PollDeadlineMs", Label = "Poll deadline (ms)", Kind = DriverSettingKind.Number, Default = "5000", Min = 100, Max = 600_000, Advanced = true },
            new DriverSetting { Key = "QuarantineMs", Label = "Quarantine (ms)", Kind = DriverSettingKind.Number, Default = "60000", Min = 1_000, Max = 86_400_000, Advanced = true },
        },
    };

    public IReadOnlyList<string> Validate(DeviceConfig config)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Setting(config, "Host"))) errors.Add("Setting 'Host' is required.");
        Number("Port", 1, 65535); Number("Rack", 0, 255); Number("Slot", 0, 255);
        Number("TimeoutMs", 100, 10_000); Number("MaxGapBytes", 0, 960);
        Number("MaxBlockBytes", 16, 960); Number("PollDeadlineMs", 100, 600_000);
        Number("QuarantineMs", 1_000, 86_400_000);
        var cpuText = Setting(config, "CpuType");
        if (cpuText.Length > 0 && !Enum.TryParse<CpuType>(cpuText, true, out _))
            errors.Add($"CpuType '{cpuText}' is invalid. Use S7200, S7300, S7400, S71200, S71500, or Logo0BA8.");

        // S7netplus does not retry for us, so the whole per-request budget is the timeout. It still
        // has to fit inside the stop deadline: the library reads on a socket timeout with no token,
        // so a poll against a silent CPU is unreachable from the engine until it expires.
        var timeoutText = Setting(config, "TimeoutMs");
        if ((timeoutText.Length == 0 || int.TryParse(timeoutText, out _))
            && DriverLimits.CheckCancellationBudget(
                "TimeoutMs", timeoutText.Length == 0 ? 3000 : int.Parse(timeoutText), "Retries", 0) is { } budget)
            errors.Add(budget);

        return errors;

        void Number(string key, int min, int max)
        {
            var text = Setting(config, key);
            if (text.Length > 0 && (!int.TryParse(text, out var n) || n < min || n > max))
                errors.Add($"Setting '{key}' must be {min}-{max} (got '{text}').");
        }
    }

    public IReadOnlyDictionary<string, string> ValidateTags(DeviceConfig config)
    {
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in config.Tags)
        {
            if (tag.DataType == DataType.String)
            {
                problems[tag.Id] = "The S7 driver does not support String tags yet.";
                continue;
            }
            try
            {
                var address = S7Address.Parse(tag.Address);
                var size = address.Bit is null ? S7Codec.SizeOf(tag.DataType) : 1;
                if (address.Bit is not null && tag.DataType != DataType.Bool)
                    problems[tag.Id] = $"A bit address must use Bool, not {tag.DataType}.";
                else if (address.Bit is null && tag.DataType == DataType.Bool)
                    problems[tag.Id] = "Bool should use a bit address such as DB1.DBX0.0 or M0.0.";
                else if (address.DeclaredSize is { } declared && address.Bit is null && declared != Math.Min(size, 4))
                    problems[tag.Id] = $"Address width is {declared} byte(s), but {tag.DataType} needs {size}.";
                else if ((long)address.ByteOffset + size > int.MaxValue)
                    problems[tag.Id] = "Address exceeds the S7 address range.";
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException)
            {
                problems[tag.Id] = $"Address '{tag.Address}': {ex.Message}";
            }
        }
        return problems;
    }

    private static string Setting(DeviceConfig config, string key) =>
        config.Settings.TryGetValue(key, out var value) ? value?.Trim() ?? "" : "";
}
