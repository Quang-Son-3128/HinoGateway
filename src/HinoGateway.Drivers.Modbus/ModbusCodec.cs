using HinoGateway.Core;

namespace HinoGateway.Drivers.Modbus;

/// <summary>
/// Converts between raw 16-bit Modbus registers and typed values.
///
/// The standard says nothing about how a 32- or 64-bit value is spread across registers, so devices
/// disagree in two independent ways and both have to be configurable:
/// <list type="bullet">
/// <item><c>wordSwap</c> — register order. Default is most-significant word first ("ABCD"); swapped
/// gives "CDAB".</item>
/// <item><c>byteSwap</c> — byte order <em>inside</em> each register ("BADC"), seen on some energy
/// meters and gateways. Combining both yields the fourth layout, "DCBA".</item>
/// </list>
/// </summary>
public static class ModbusCodec
{
    public static object DecodeRegisters(DataType dt, ushort[] regs, bool wordSwap, bool byteSwap = false)
    {
        var r = byteSwap ? SwapBytes(regs) : regs;

        switch (dt)
        {
            case DataType.Bool:
                return r[0] != 0;
            case DataType.Int16:
                return (short)r[0];
            case DataType.UInt16:
                return r[0];
            case DataType.Int32:
                return unchecked((int)Combine32(r, wordSwap));
            case DataType.UInt32:
                return Combine32(r, wordSwap);
            case DataType.Float32:
                return BitConverter.Int32BitsToSingle(unchecked((int)Combine32(r, wordSwap)));
            case DataType.Int64:
                return unchecked((long)Combine64(r, wordSwap));
            case DataType.UInt64:
                return Combine64(r, wordSwap);
            case DataType.Float64:
                return BitConverter.Int64BitsToDouble(unchecked((long)Combine64(r, wordSwap)));
            default:
                // Never guess: silently decoding a String as a 16-bit number would hand the operator
                // a wrong value carrying Quality.Good. The caller turns this into Bad + a reason.
                throw new NotSupportedException($"The Modbus driver cannot decode {dt} values.");
        }
    }

    /// <summary>Extract one bit of a register (0 = least significant).</summary>
    public static bool DecodeBit(ushort register, int bit, bool byteSwap = false)
    {
        if (bit is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(bit), bit, "Bit index must be 0-15.");
        var value = byteSwap ? SwapBytes(register) : register;
        return ((value >> bit) & 1) != 0;
    }

    public static ushort[] EncodeRegisters(DataType dt, object value, bool wordSwap, bool byteSwap = false)
    {
        var regs = dt switch
        {
            DataType.Bool => [(ushort)(ToBool(value) ? 1 : 0)],
            DataType.Int16 => [unchecked((ushort)Convert.ToInt16(value))],
            DataType.UInt16 => [Convert.ToUInt16(value)],
            DataType.Int32 => Split32(unchecked((uint)Convert.ToInt32(value)), wordSwap),
            DataType.UInt32 => Split32(Convert.ToUInt32(value), wordSwap),
            DataType.Float32 => Split32(unchecked((uint)BitConverter.SingleToInt32Bits(Convert.ToSingle(value))), wordSwap),
            DataType.Int64 => Split64(unchecked((ulong)Convert.ToInt64(value)), wordSwap),
            DataType.UInt64 => Split64(Convert.ToUInt64(value), wordSwap),
            DataType.Float64 => Split64(unchecked((ulong)BitConverter.DoubleToInt64Bits(Convert.ToDouble(value))), wordSwap),
            _ => throw new NotSupportedException($"The Modbus driver cannot encode {dt} values."),
        };

        return byteSwap ? SwapBytes(regs) : regs;
    }

    public static bool ToBool(object value) => value switch
    {
        bool b => b,
        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1",
        _ => Convert.ToDouble(value) != 0,
    };

    private static ushort SwapBytes(ushort v) => (ushort)((v >> 8) | (v << 8));

    private static ushort[] SwapBytes(ushort[] regs)
    {
        var swapped = new ushort[regs.Length];
        for (var i = 0; i < regs.Length; i++)
            swapped[i] = SwapBytes(regs[i]);
        return swapped;
    }

    private static uint Combine32(ushort[] r, bool swap)
    {
        var (hi, lo) = swap ? (r[1], r[0]) : (r[0], r[1]);
        return ((uint)hi << 16) | lo;
    }

    private static ulong Combine64(ushort[] r, bool swap)
    {
        var o = swap ? new[] { r[3], r[2], r[1], r[0] } : r;
        return ((ulong)o[0] << 48) | ((ulong)o[1] << 32) | ((ulong)o[2] << 16) | o[3];
    }

    private static ushort[] Split32(uint v, bool swap)
    {
        var hi = (ushort)(v >> 16);
        var lo = (ushort)(v & 0xFFFF);
        return swap ? [lo, hi] : [hi, lo];
    }

    private static ushort[] Split64(ulong v, bool swap)
    {
        var w = new[]
        {
            (ushort)(v >> 48),
            (ushort)(v >> 32),
            (ushort)(v >> 16),
            (ushort)v,
        };
        return swap ? [w[3], w[2], w[1], w[0]] : w;
    }
}
