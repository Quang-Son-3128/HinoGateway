using HinoGateway.Core;
using HinoGateway.Drivers.Modbus;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// Unsigned types, single bits inside a register and byte order — the three things real devices need
/// that a textbook Modbus implementation leaves out.
/// </summary>
public class ModbusDataTypeTests
{
    [Fact]
    public void Unsigned_registers_are_not_read_as_negative_numbers()
    {
        // 0xFFFF is 65535 unsigned, -1 signed. Reading a counter as Int16 is the classic field bug.
        Assert.Equal((ushort)65535, ModbusCodec.DecodeRegisters(DataType.UInt16, [0xFFFF], wordSwap: false));
        Assert.Equal((short)-1, ModbusCodec.DecodeRegisters(DataType.Int16, [0xFFFF], wordSwap: false));
    }

    [Fact]
    public void Unsigned_32_and_64_bit_values_round_trip()
    {
        var regs32 = ModbusCodec.EncodeRegisters(DataType.UInt32, 4_000_000_000u, wordSwap: false);
        Assert.Equal(4_000_000_000u, ModbusCodec.DecodeRegisters(DataType.UInt32, regs32, wordSwap: false));

        var regs64 = ModbusCodec.EncodeRegisters(DataType.UInt64, 18_000_000_000_000_000_000ul, wordSwap: false);
        Assert.Equal(18_000_000_000_000_000_000ul, ModbusCodec.DecodeRegisters(DataType.UInt64, regs64, wordSwap: false));
    }

    [Fact]
    public void Unsigned_types_occupy_the_right_number_of_registers()
    {
        Assert.Equal(1, ModbusAddress.RegisterCount(DataType.UInt16));
        Assert.Equal(2, ModbusAddress.RegisterCount(DataType.UInt32));
        Assert.Equal(4, ModbusAddress.RegisterCount(DataType.UInt64));
    }

    [Theory]
    [InlineData("HR:10.0", 0)]
    [InlineData("HR:10.7", 7)]
    [InlineData("40011.15", 15)]
    public void A_bit_inside_a_register_can_be_addressed(string address, int expectedBit)
    {
        var parsed = ModbusAddress.Parse(address);

        Assert.Equal(ModbusRegisterType.HoldingRegister, parsed.Type);
        Assert.Equal(10, parsed.Address);
        Assert.Equal(expectedBit, parsed.Bit);
    }

    [Theory]
    [InlineData("HR:10.16")]
    [InlineData("HR:10.-1")]
    [InlineData("HR:10.x")]
    public void An_impossible_bit_index_is_refused(string address)
    {
        var ex = Assert.Throws<FormatException>(() => ModbusAddress.Parse(address));
        Assert.Contains("Bit index", ex.Message);
    }

    [Fact]
    public void A_bit_suffix_makes_no_sense_on_a_coil()
    {
        Assert.Throws<FormatException>(() => ModbusAddress.Parse("CO:3.1"));
    }

    [Fact]
    public void Bits_are_extracted_least_significant_first()
    {
        const ushort word = 0b0000_0000_1010_0101;

        Assert.True(ModbusCodec.DecodeBit(word, 0));
        Assert.False(ModbusCodec.DecodeBit(word, 1));
        Assert.True(ModbusCodec.DecodeBit(word, 2));
        Assert.True(ModbusCodec.DecodeBit(word, 5));
        Assert.True(ModbusCodec.DecodeBit(word, 7));
        Assert.False(ModbusCodec.DecodeBit(word, 15));
    }

    [Fact]
    public void Byte_swap_is_independent_of_word_swap()
    {
        // 0x1234 with the bytes swapped inside the register is 0x3412.
        Assert.Equal((ushort)0x3412, ModbusCodec.DecodeRegisters(DataType.UInt16, [0x1234], wordSwap: false, byteSwap: true));

        // A 32-bit value has four layouts; each combination must be distinct and reversible.
        ushort[] abcd = [0x1234, 0x5678];
        var plain = (uint)ModbusCodec.DecodeRegisters(DataType.UInt32, abcd, wordSwap: false);
        var words = (uint)ModbusCodec.DecodeRegisters(DataType.UInt32, abcd, wordSwap: true);
        var bytes = (uint)ModbusCodec.DecodeRegisters(DataType.UInt32, abcd, wordSwap: false, byteSwap: true);
        var both = (uint)ModbusCodec.DecodeRegisters(DataType.UInt32, abcd, wordSwap: true, byteSwap: true);

        Assert.Equal(0x12345678u, plain);
        Assert.Equal(0x56781234u, words);
        Assert.Equal(0x34127856u, bytes);
        Assert.Equal(0x78563412u, both);
        Assert.Equal(4, new[] { plain, words, bytes, both }.Distinct().Count());
    }

    [Fact]
    public void Byte_swapped_values_round_trip_through_encode_and_decode()
    {
        var regs = ModbusCodec.EncodeRegisters(DataType.Float32, 123.5f, wordSwap: true, byteSwap: true);
        var back = ModbusCodec.DecodeRegisters(DataType.Float32, regs, wordSwap: true, byteSwap: true);

        Assert.Equal(123.5f, Assert.IsType<float>(back));
    }

    [Fact]
    public async Task Writing_a_single_bit_of_a_register_is_refused_rather_than_read_modify_written()
    {
        var config = new DeviceConfig
        {
            DeviceId = "MB-1",
            Driver = "ModbusTCP",
            ScanInterval = TimeSpan.FromMilliseconds(500),
            Settings = new Dictionary<string, string> { ["Host"] = "127.0.0.1" },
            Tags = [new TagDefinition { Id = "A/Flag", Name = "Flag", Address = "HR:10.3", DataType = DataType.Bool }],
        };

        var problems = new ModbusTcpDriverFactory().Validate(config);
        Assert.Empty(problems);   // reading it is fine...

        // ...writing it is not: the other 15 bits belong to the PLC.
        await using var driver = new ModbusTcpDriver(config);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => driver.WriteAsync("A/Flag", true, default));
        Assert.Contains("single bit", ex.Message);
    }

    [Fact]
    public void A_bit_address_must_be_declared_as_a_bool()
    {
        var config = new DeviceConfig
        {
            DeviceId = "MB-1",
            Driver = "ModbusTCP",
            ScanInterval = TimeSpan.FromMilliseconds(500),
            Settings = new Dictionary<string, string> { ["Host"] = "127.0.0.1" },
            Tags = [new TagDefinition { Id = "A/Flag", Name = "Flag", Address = "HR:10.3", DataType = DataType.Int16 }],
        };

        Assert.Contains(new ModbusTcpDriverFactory().ValidateTags(config).Values, p => p.Contains("Bool"));
    }
}
