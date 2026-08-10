using HinoGateway.Core;
using HinoGateway.Drivers.SiemensS7;

namespace HinoGateway.Engine.Tests;

public sealed class SiemensS7RecoveryTests
{
    [Theory]
    [InlineData("DB2[2]", S7Area.DataBlock, 2, 2, null)]
    [InlineData("DB0[0].0", S7Area.DataBlock, 0, 0, 0)]
    [InlineData("MW100", S7Area.Merker, 0, 100, null)]
    public void Parses_addresses_from_pre_incident_export(string text, S7Area area, int db, int offset, int? bit)
    {
        var address = S7Address.Parse(text);
        Assert.Equal(area, address.Area);
        Assert.Equal(db, address.DbNumber);
        Assert.Equal(offset, address.ByteOffset);
        Assert.Equal(bit, address.Bit);
    }

    [Fact]
    public void UInt16_is_big_endian()
    {
        Assert.Equal((ushort)0x1234, S7Codec.Decode(DataType.UInt16, [0x12, 0x34], null));
        Assert.Equal(new byte[] { 0x12, 0x34 }, S7Codec.Encode(DataType.UInt16, 0x1234));
    }

    [Fact]
    public void Planner_keeps_different_scan_rates_in_different_blocks()
    {
        var fast = Point("fast", "DB1.DBW0", TimeSpan.FromSeconds(1));
        var slow = Point("slow", "DB1.DBW2", TimeSpan.FromMinutes(1));
        var blocks = S7ReadPlan.Build([fast, slow], 8, 200);
        Assert.Equal(2, blocks.Count);
        Assert.Contains(blocks, b => b.ScanInterval == TimeSpan.FromSeconds(1));
        Assert.Contains(blocks, b => b.ScanInterval == TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Factory_rejects_width_mismatch_per_tag()
    {
        var config = new DeviceConfig
        {
            DeviceId = "S7", Driver = "SiemensS7",
            Settings = new Dictionary<string, string> { ["Host"] = "192.168.0.1" },
            Tags = [new TagDefinition { Id = "bad", Name = "bad", Address = "DB1.DBW2", DataType = DataType.Float32 }],
        };
        var problems = new SiemensS7DriverFactory().ValidateTags(config);
        Assert.Contains("width", problems["bad"], StringComparison.OrdinalIgnoreCase);
    }

    private static S7Point Point(string id, string address, TimeSpan rate)
    {
        var tag = new TagDefinition { Id = id, Name = id, Address = address, DataType = DataType.UInt16, ScanInterval = rate };
        return new S7Point(tag, S7Address.Parse(address), 2);
    }
}
