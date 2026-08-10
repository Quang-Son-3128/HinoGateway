using HinoGateway.Core;

namespace HinoGateway.Drivers.Modbus;

public enum ModbusRegisterType
{
    Coil,            // read/write bit   (FC 01 / 05)
    DiscreteInput,   // read-only bit    (FC 02)
    InputRegister,   // read-only 16-bit (FC 04)
    HoldingRegister, // read/write 16-bit(FC 03 / 06 / 16)
}

/// <summary>
/// A parsed Modbus data point: register type + 0-based address, plus how many 16-bit registers
/// the tag's data type occupies. Supports two address syntaxes:
/// <list type="bullet">
/// <item>Prefixed, 0-based: <c>HR:0</c>, <c>IR:10</c>, <c>CO:3</c>, <c>DI:1</c> (colon optional).</item>
/// <item>Classic 5-digit: <c>40001</c> (holding), <c>30001</c> (input), <c>10001</c> (discrete),
/// <c>00001</c> (coil).</item>
/// </list>
/// A bare number (e.g. <c>0</c>) means holding register at that 0-based address.
///
/// A register address may name a single bit inside it with <c>.bit</c> (0 = least significant):
/// <c>HR:10.3</c> or <c>40011.3</c>. Status words are packed this way on most PLCs, so without it
/// every flag would need its own scaling trick in the field.
/// </summary>
public readonly record struct ModbusAddress(ModbusRegisterType Type, ushort Address, int? Bit = null)
{
    /// <summary>How many 16-bit registers a value of this type occupies.</summary>
    public static int RegisterCount(DataType dt) => dt switch
    {
        DataType.Int64 or DataType.UInt64 or DataType.Float64 => 4,
        DataType.Int32 or DataType.UInt32 or DataType.Float32 => 2,
        _ => 1,
    };

    public static ModbusAddress Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new FormatException("Empty Modbus address.");

        var s = raw.Trim().Replace(":", "").ToUpperInvariant();

        // Optional ".bit" suffix selecting one bit of a register.
        int? bit = null;
        var dot = s.IndexOf('.');
        if (dot >= 0)
        {
            var bitText = s[(dot + 1)..];
            if (!int.TryParse(bitText, out var bitIndex) || bitIndex is < 0 or > 15)
                throw new FormatException($"Bit index must be 0-15 in '{raw}'.");
            bit = bitIndex;
            s = s[..dot];
        }

        var address = ParseArea(s, raw);
        if (bit is null)
            return address;

        if (address.Type is ModbusRegisterType.Coil or ModbusRegisterType.DiscreteInput)
            throw new FormatException($"'{raw}' already addresses a single bit — a .bit suffix only applies to registers.");

        return address with { Bit = bit };
    }

    private static ModbusAddress ParseArea(string s, string raw)
    {
        (ModbusRegisterType type, string digits)? prefixed = s switch
        {
            _ when s.StartsWith("HR") => (ModbusRegisterType.HoldingRegister, s[2..]),
            _ when s.StartsWith("IR") => (ModbusRegisterType.InputRegister, s[2..]),
            _ when s.StartsWith("DI") => (ModbusRegisterType.DiscreteInput, s[2..]),
            _ when s.StartsWith("CO") => (ModbusRegisterType.Coil, s[2..]),
            _ => null,
        };

        if (prefixed is { } p)
            return new ModbusAddress(p.type, ParseUShort(p.digits));

        // Classic 5-digit convention (1-based within its bank).
        if (s.Length == 5 && s.All(char.IsDigit))
        {
            var n = int.Parse(s);
            return s[0] switch
            {
                '4' => new ModbusAddress(ModbusRegisterType.HoldingRegister, (ushort)(n - 40001)),
                '3' => new ModbusAddress(ModbusRegisterType.InputRegister, (ushort)(n - 30001)),
                '1' => new ModbusAddress(ModbusRegisterType.DiscreteInput, (ushort)(n - 10001)),
                '0' => new ModbusAddress(ModbusRegisterType.Coil, (ushort)(n - 1)),
                _ => throw new FormatException($"Unrecognized Modbus address '{raw}'."),
            };
        }

        // Bare number → holding register, 0-based.
        return new ModbusAddress(ModbusRegisterType.HoldingRegister, ParseUShort(s));
    }

    private static ushort ParseUShort(string digits)
    {
        if (!ushort.TryParse(digits, out var v))
            throw new FormatException($"Invalid Modbus register address '{digits}'.");
        return v;
    }
}
