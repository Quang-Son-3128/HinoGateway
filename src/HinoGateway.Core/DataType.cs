namespace HinoGateway.Core;

/// <summary>
/// Engineering data type of a tag. Drivers map their raw protocol representation onto these.
/// </summary>
public enum DataType
{
    Bool = 0,
    Int16 = 1,
    Int32 = 2,
    Int64 = 3,
    Float32 = 4,
    Float64 = 5,
    String = 6,

    // Appended (never renumber — these values live in saved project files).
    UInt16 = 7,
    UInt32 = 8,
    UInt64 = 9,
}

public static class DataTypes
{
    /// <summary>Is this a whole-number type? (Used for range checks and register sizing.)</summary>
    public static bool IsInteger(this DataType type) => type is
        DataType.Int16 or DataType.Int32 or DataType.Int64 or
        DataType.UInt16 or DataType.UInt32 or DataType.UInt64;

    /// <summary>Bytes on the wire — 2, 4 or 8 — for the numeric types.</summary>
    public static int ByteCount(this DataType type) => type switch
    {
        DataType.Int64 or DataType.UInt64 or DataType.Float64 => 8,
        DataType.Int32 or DataType.UInt32 or DataType.Float32 => 4,
        _ => 2,
    };
}
