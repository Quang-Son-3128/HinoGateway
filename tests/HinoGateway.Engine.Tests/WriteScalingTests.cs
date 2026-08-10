using HinoGateway.Core;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// The write path must be the exact inverse of the read path. Getting this wrong sends a plausible
/// but wrong number to a PLC — the value reads back scaled, so nobody notices until something moves.
/// </summary>
public class WriteScalingTests
{
    private static TagDefinition Tag(DataType type = DataType.Int16, double scale = 1.0, double offset = 0.0) => new()
    {
        Id = "Area/Temp",
        Name = "Temp",
        Address = "40001",
        DataType = type,
        Scale = scale,
        Offset = offset,
    };

    [Theory]
    // scale, offset, engineering value, expected raw
    [InlineData(0.1, 0.0, 50.0, 500)]
    // 5.05/0.1 is 50.499999999999993 in binary floating point, which would round DOWN to 50 —
    // the conversion must honour the decimal value the operator actually typed.
    [InlineData(0.1, 0.0, 5.05, 51)]
    [InlineData(1.0, 0.0, 1234.0, 1234)]  // identity passes straight through
    [InlineData(2.0, 0.0, 50.0, 25)]
    [InlineData(1.0, 100.0, 150.0, 50)]   // offset removed first
    [InlineData(0.1, -40.0, -35.0, 50)]
    public void Engineering_values_are_converted_back_to_raw(double scale, double offset, double engineering, int expectedRaw)
    {
        var tag = Tag(DataType.Int16, scale, offset);

        Assert.True(Scaling.ToRaw(tag, engineering, out var raw, out var error));

        Assert.Null(error);
        Assert.Equal((short)expectedRaw, raw);
    }

    [Fact]
    public void Read_then_write_round_trips_through_the_same_scaling()
    {
        var tag = Tag(DataType.Int16, scale: 0.1, offset: 5);

        // The device holds raw 500 → the operator sees 55.
        var displayed = Scaling.Apply(tag, TagValue.Good((short)500, DateTimeOffset.UtcNow));
        Assert.Equal(55.0, Assert.IsType<double>(displayed.Value), 6);

        // Writing that same 55 back must land on raw 500 again, not 55.
        Assert.True(Scaling.ToRaw(tag, 55.0, out var raw, out _));
        Assert.Equal((short)500, raw);
    }

    [Fact]
    public void A_scale_of_zero_is_refused_because_it_has_no_inverse()
    {
        Assert.False(Scaling.ToRaw(Tag(scale: 0.0), 42.0, out _, out var error));
        Assert.Contains("Scale = 0", error);
    }

    [Theory]
    [InlineData(DataType.Int16, 40000.0)]
    [InlineData(DataType.Int16, -40000.0)]
    [InlineData(DataType.Int32, 3e9)]
    public void Out_of_range_values_are_refused_instead_of_wrapping_around(DataType type, double value)
    {
        Assert.False(Scaling.ToRaw(Tag(type), value, out _, out var error));
        Assert.Contains("out of range", error);
    }

    [Fact]
    public void Scaling_can_push_an_in_range_value_out_of_range()
    {
        // 4000 looks harmless, but at scale 0.1 the device would need raw 40000 — beyond Int16.
        Assert.False(Scaling.ToRaw(Tag(DataType.Int16, scale: 0.1), 4000.0, out _, out var error));
        Assert.Contains("out of range", error);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Non_finite_values_are_refused(double value)
    {
        Assert.False(Scaling.ToRaw(Tag(DataType.Float64), value, out _, out var error));
        Assert.Contains("finite", error);
    }

    [Fact]
    public void Booleans_are_not_scaled_in_either_direction()
    {
        var tag = Tag(DataType.Bool, scale: 0.1, offset: 3);

        Assert.True(Scaling.ToRaw(tag, true, out var raw, out _));
        Assert.Equal(true, raw);
    }

    [Fact]
    public void Numeric_text_is_accepted_but_nonsense_is_not()
    {
        var tag = Tag(DataType.Int16, scale: 0.1);

        Assert.True(Scaling.ToRaw(tag, "50", out var raw, out _));
        Assert.Equal((short)500, raw);

        Assert.False(Scaling.ToRaw(tag, "not a number", out _, out var error));
        Assert.Contains("not a number", error);
    }

    [Fact]
    public void String_tags_cannot_be_written_yet()
    {
        Assert.False(Scaling.ToRaw(Tag(DataType.String), "hello", out _, out var error));
        Assert.Contains("not supported", error);
    }

    [Fact]
    public void Float_targets_keep_their_fraction()
    {
        Assert.True(Scaling.ToRaw(Tag(DataType.Float32, scale: 0.5), 10.0, out var raw, out _));
        Assert.Equal(20f, Assert.IsType<float>(raw));

        Assert.True(Scaling.ToRaw(Tag(DataType.Float64, scale: 0.1), 1.0, out var raw64, out _));
        Assert.Equal(10.0, Assert.IsType<double>(raw64), 6);
    }
}
