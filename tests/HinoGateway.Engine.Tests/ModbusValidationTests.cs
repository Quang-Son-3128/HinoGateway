using HinoGateway.Core;
using HinoGateway.Drivers.Modbus;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// Validation is what turns "the gateway won't start" into "this one tag is wrong": every mistake a
/// driver constructor would throw on must be reported here instead, per tag, all at once.
/// </summary>
public class ModbusValidationTests
{
    private static readonly ModbusTcpDriverFactory Factory = new();

    private static DeviceConfig Device(Dictionary<string, string>? settings = null, params TagDefinition[] tags) => new()
    {
        DeviceId = "MB-1",
        Driver = "ModbusTCP",
        ScanInterval = TimeSpan.FromMilliseconds(500),
        Settings = settings ?? new Dictionary<string, string> { ["Host"] = "127.0.0.1" },
        Tags = tags,
    };

    private static TagDefinition Tag(string address, DataType type = DataType.Int16, string name = "T") =>
        new() { Id = $"A/{name}", Name = name, Address = address, DataType = type };

    [Fact]
    public void A_usable_configuration_reports_nothing()
    {
        var problems = Factory.Validate(Device(
            new Dictionary<string, string>
            {
                ["Host"] = "10.0.0.5", ["Port"] = "502", ["UnitId"] = "1",
                ["TimeoutMs"] = "1000", ["Retries"] = "1", ["WordSwap"] = "true",
            },
            Tag("40001"), Tag("HR:10", DataType.Float32), Tag("00001", DataType.Bool)));

        Assert.Empty(problems);
    }

    [Fact]
    public void A_missing_host_is_reported()
    {
        var problems = Factory.Validate(Device(new Dictionary<string, string>()));
        Assert.Contains(problems, p => p.Contains("Host"));
    }

    [Theory]
    [InlineData("Port", "70000")]
    [InlineData("Port", "abc")]
    [InlineData("UnitId", "0")]
    [InlineData("TimeoutMs", "10")]
    [InlineData("Retries", "99")]
    [InlineData("WordSwap", "maybe")]
    public void Bad_settings_are_reported_by_name(string key, string value)
    {
        var settings = new Dictionary<string, string> { ["Host"] = "127.0.0.1", [key] = value };

        var problems = Factory.Validate(Device(settings));

        Assert.Contains(problems, p => p.Contains(key));
    }

    [Fact]
    public void An_unparseable_address_names_the_tag_and_the_address()
    {
        var problems = Factory.ValidateTags(Device(null, Tag("not-an-address", name: "Broken"))).Values.ToList();

        var problem = Assert.Single(problems);
        Assert.Contains("Broken", problem);
        Assert.Contains("not-an-address", problem);
    }

    [Fact]
    public void Every_bad_tag_is_reported_not_just_the_first()
    {
        var problems = Factory.ValidateTags(Device(null,
            Tag("40001"),                       // fine
            Tag("nope", name: "First"),
            Tag("also-nope", name: "Second"))).Values.ToList();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("First"));
        Assert.Contains(problems, p => p.Contains("Second"));
    }

    [Fact]
    public void String_tags_are_refused_rather_than_silently_decoded_as_a_number()
    {
        var problems = Factory.ValidateTags(Device(null, Tag("40001", DataType.String, "Label"))).Values;

        Assert.Contains(problems, p => p.Contains("String"));
    }

    [Fact]
    public void A_bit_address_must_carry_a_bool()
    {
        var problems = Factory.ValidateTags(Device(null, Tag("00001", DataType.Int32, "Coil"))).Values;

        Assert.Contains(problems, p => p.Contains("Bool"));
    }

    [Fact]
    public void A_multi_register_type_may_not_run_past_the_end_of_the_address_space()
    {
        var problems = Factory.ValidateTags(Device(null, Tag("HR:65535", DataType.Float64, "Edge"))).Values;

        Assert.Contains(problems, p => p.Contains("address space"));
    }

    [Fact]
    public void The_codec_refuses_unsupported_types_instead_of_guessing()
    {
        Assert.Throws<NotSupportedException>(() =>
            ModbusCodec.DecodeRegisters(DataType.String, [0x41, 0x42], wordSwap: false));
        Assert.Throws<NotSupportedException>(() =>
            ModbusCodec.EncodeRegisters(DataType.String, "AB", wordSwap: false));
    }
}
