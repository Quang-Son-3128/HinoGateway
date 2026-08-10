using System.Buffers.Binary;
using HinoDataType = HinoGateway.Core.DataType;

namespace HinoGateway.Drivers.SiemensS7;

public static class S7Codec
{
    public static int SizeOf(HinoDataType type) => type switch
    {
        HinoDataType.Bool => 1,
        HinoDataType.Int16 or HinoDataType.UInt16 => 2,
        HinoDataType.Int32 or HinoDataType.UInt32 or HinoDataType.Float32 => 4,
        HinoDataType.Int64 or HinoDataType.UInt64 or HinoDataType.Float64 => 8,
        _ => throw new NotSupportedException($"S7 driver does not support {type} tags."),
    };

    public static object Decode(HinoDataType type, ReadOnlySpan<byte> bytes, int? bit)
    {
        if (bit is { } b) return (bytes[0] & (1 << b)) != 0;
        return type switch
        {
            HinoDataType.Bool => bytes[0] != 0,
            HinoDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(bytes),
            HinoDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(bytes),
            HinoDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(bytes),
            HinoDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
            HinoDataType.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes)),
            HinoDataType.Int64 => BinaryPrimitives.ReadInt64BigEndian(bytes),
            HinoDataType.UInt64 => BinaryPrimitives.ReadUInt64BigEndian(bytes),
            HinoDataType.Float64 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes)),
            _ => throw new NotSupportedException($"S7 driver cannot decode {type}."),
        };
    }

    public static byte[] Encode(HinoDataType type, object value)
    {
        var bytes = new byte[SizeOf(type)];
        switch (type)
        {
            case HinoDataType.Bool: bytes[0] = Convert.ToBoolean(value) ? (byte)1 : (byte)0; break;
            case HinoDataType.Int16: BinaryPrimitives.WriteInt16BigEndian(bytes, Convert.ToInt16(value)); break;
            case HinoDataType.UInt16: BinaryPrimitives.WriteUInt16BigEndian(bytes, Convert.ToUInt16(value)); break;
            case HinoDataType.Int32: BinaryPrimitives.WriteInt32BigEndian(bytes, Convert.ToInt32(value)); break;
            case HinoDataType.UInt32: BinaryPrimitives.WriteUInt32BigEndian(bytes, Convert.ToUInt32(value)); break;
            case HinoDataType.Float32: BinaryPrimitives.WriteInt32BigEndian(bytes, BitConverter.SingleToInt32Bits(Convert.ToSingle(value))); break;
            case HinoDataType.Int64: BinaryPrimitives.WriteInt64BigEndian(bytes, Convert.ToInt64(value)); break;
            case HinoDataType.UInt64: BinaryPrimitives.WriteUInt64BigEndian(bytes, Convert.ToUInt64(value)); break;
            case HinoDataType.Float64: BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(Convert.ToDouble(value))); break;
            default: throw new NotSupportedException($"S7 driver cannot encode {type}.");
        }
        return bytes;
    }
}
