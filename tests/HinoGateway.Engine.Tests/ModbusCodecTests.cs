using HinoGateway.Core;
using HinoGateway.Drivers.Modbus;

namespace HinoGateway.Engine.Tests;

public class ModbusAddressTests
{
    [Theory]
    [InlineData("HR:0", ModbusRegisterType.HoldingRegister, 0)]
    [InlineData("hr10", ModbusRegisterType.HoldingRegister, 10)]
    [InlineData("IR:5", ModbusRegisterType.InputRegister, 5)]
    [InlineData("CO:3", ModbusRegisterType.Coil, 3)]
    [InlineData("DI:1", ModbusRegisterType.DiscreteInput, 1)]
    [InlineData("40001", ModbusRegisterType.HoldingRegister, 0)]
    [InlineData("30001", ModbusRegisterType.InputRegister, 0)]
    [InlineData("10001", ModbusRegisterType.DiscreteInput, 0)]
    [InlineData("00001", ModbusRegisterType.Coil, 0)]
    [InlineData("7", ModbusRegisterType.HoldingRegister, 7)] // bare number -> holding
    public void Parses_addresses(string raw, ModbusRegisterType type, int address)
    {
        var a = ModbusAddress.Parse(raw);
        Assert.Equal(type, a.Type);
        Assert.Equal((ushort)address, a.Address);
    }
}

public class ModbusCodecTests
{
    [Fact]
    public void Decodes_int16()
    {
        Assert.Equal((short)14, (short)ModbusCodec.DecodeRegisters(DataType.Int16, [14], false));
        Assert.Equal((short)-1, (short)ModbusCodec.DecodeRegisters(DataType.Int16, [0xFFFF], false));
    }

    [Fact]
    public void Decodes_int32_big_endian_and_word_swapped()
    {
        // 0x00010002 = 65538
        Assert.Equal(65538, (int)ModbusCodec.DecodeRegisters(DataType.Int32, [0x0001, 0x0002], false));
        Assert.Equal(65538, (int)ModbusCodec.DecodeRegisters(DataType.Int32, [0x0002, 0x0001], true));
    }

    [Fact]
    public void Float32_round_trips_through_encode_decode()
    {
        var regs = ModbusCodec.EncodeRegisters(DataType.Float32, 123.5f, false);
        var back = (float)ModbusCodec.DecodeRegisters(DataType.Float32, regs, false);
        Assert.Equal(123.5f, back);
    }
}

public class ScalingTests
{
    [Fact]
    public void Applies_linear_scale_and_offset_to_good_numeric()
    {
        var tag = new TagDefinition { Id = "t", Name = "t", Address = "0", DataType = DataType.Int16, Scale = 0.1, Offset = 5 };
        var raw = TagValue.Good((short)14, DateTimeOffset.UtcNow);
        var scaled = Scaling.Apply(tag, raw);
        Assert.Equal(6.4, (double)scaled.Value!, 6); // 14*0.1 + 5
    }

    [Fact]
    public void Leaves_bad_quality_and_non_numeric_untouched()
    {
        var tag = new TagDefinition { Id = "t", Name = "t", Address = "0", Scale = 2 };
        var bad = TagValue.Bad("x", DateTimeOffset.UtcNow);
        Assert.Same(bad, Scaling.Apply(tag, bad));
    }
}
