using HinoGateway.Core;

namespace HinoGateway.Engine.Tests;

public class TagWritePolicyTests
{
    private static TagDefinition Tag(bool allow, double? min = null, double? max = null) => new()
    {
        Id = "Plant/Limit", Name = "Limit", Address = "HR:0", AllowWrite = allow,
        WriteMin = min, WriteMax = max,
    };

    [Fact]
    public void Readable_tag_is_not_implicitly_writable() =>
        Assert.Throws<UnauthorizedAccessException>(() => TagWritePolicy.EnsureAllowed(Tag(false), 10));

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    public void Engineering_limits_are_enforced(double value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TagWritePolicy.EnsureAllowed(Tag(true, 0, 100), value));

    [Fact]
    public void Boundary_values_are_allowed()
    {
        TagWritePolicy.EnsureAllowed(Tag(true, 0, 100), 0);
        TagWritePolicy.EnsureAllowed(Tag(true, 0, 100), 100);
    }
}
