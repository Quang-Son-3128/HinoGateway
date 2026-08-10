using HinoGateway.Core;
using HinoGateway.Drivers.Modbus;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// The read plan decides what actually goes on the wire, so these tests are written in terms of
/// requests-per-scan — the number an engineer watching a busy line cares about.
/// </summary>
public class ModbusReadPlanTests
{
    private static ModbusPoint Point(string address, DataType type = DataType.Int16, string name = "T")
    {
        var addr = ModbusAddress.Parse(address);
        return new ModbusPoint(
            new TagDefinition { Id = $"A/{name}", Name = name, Address = address, DataType = type },
            addr,
            ModbusAddress.RegisterCount(type));
    }

    private static List<ModbusReadBlock> Plan(params ModbusPoint[] points) =>
        ModbusReadPlan.Build(points, maxRegisterGap: 8, maxBitGap: 64);

    [Fact]
    public void Adjacent_points_share_one_request()
    {
        var blocks = Plan(Point("HR:0"), Point("HR:1", name: "B"), Point("HR:2", name: "C"));

        var block = Assert.Single(blocks);
        Assert.Equal(0, block.Start);
        Assert.Equal(3, block.Count);
        Assert.Equal(3, block.Points.Count);
    }

    [Fact]
    public void A_small_gap_is_bridged_because_a_second_round_trip_costs_more()
    {
        // 0 and 5: four unwanted registers, still one request.
        var blocks = Plan(Point("HR:0"), Point("HR:5", name: "B"));

        var block = Assert.Single(blocks);
        Assert.Equal(0, block.Start);
        Assert.Equal(6, block.Count);
    }

    [Fact]
    public void A_large_gap_earns_its_own_request()
    {
        var blocks = Plan(Point("HR:0"), Point("HR:100", name: "B"));

        Assert.Equal(2, blocks.Count);
        Assert.Equal(1, blocks[0].Count);
        Assert.Equal(100, blocks[1].Start);
        Assert.Equal(1, blocks[1].Count);
    }

    [Fact]
    public void Two_far_apart_points_no_longer_cost_eighty_requests()
    {
        // The old min..max scan read 10 001 registers for this, i.e. 81 maximum-size requests.
        var blocks = Plan(Point("HR:0"), Point("HR:10000", name: "Far"));

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(1, b.Count));
        Assert.Equal(2, blocks.Sum(b => b.Count));
    }

    [Fact]
    public void A_block_never_exceeds_the_protocol_limit()
    {
        // 200 consecutive registers cannot be one request (the limit is 125).
        var points = Enumerable.Range(0, 200).Select(i => Point($"HR:{i}", name: $"T{i}")).ToArray();

        var blocks = Plan(points);

        Assert.All(blocks, b => Assert.True(b.Count <= ModbusReadPlan.MaxRegistersPerRead,
            $"block of {b.Count} exceeds the protocol limit"));
        Assert.Equal(200, blocks.Sum(b => b.Points.Count));
    }

    [Fact]
    public void A_multi_register_type_is_covered_completely()
    {
        var blocks = Plan(Point("HR:10", DataType.Float64));   // needs 10..13

        var block = Assert.Single(blocks);
        Assert.Equal(10, block.Start);
        Assert.Equal(4, block.Count);
    }

    [Fact]
    public void Each_area_is_planned_separately()
    {
        var blocks = Plan(
            Point("HR:0"), Point("IR:0", name: "B"),
            Point("CO:0", DataType.Bool, "C"), Point("DI:0", DataType.Bool, "D"));

        Assert.Equal(4, blocks.Count);
        Assert.Equal(4, blocks.Select(b => b.Area).Distinct().Count());
    }

    [Fact]
    public void Overlapping_points_do_not_widen_the_block_twice()
    {
        // A Float32 at 0 covers 0..1, and an Int16 at 1 sits inside it.
        var blocks = Plan(Point("HR:0", DataType.Float32), Point("HR:1", name: "Inside"));

        var block = Assert.Single(blocks);
        Assert.Equal(0, block.Start);
        Assert.Equal(2, block.Count);
    }

    [Fact]
    public void Splitting_halves_a_refused_block_and_tightens_the_bounds()
    {
        var blocks = Plan(Point("HR:0"), Point("HR:1", name: "B"), Point("HR:2", name: "C"), Point("HR:3", name: "D"));
        var block = Assert.Single(blocks);

        var halves = ModbusReadPlan.Split(block);

        Assert.Equal(2, halves.Count);
        Assert.Equal(2, halves[0].Points.Count);
        Assert.Equal(2, halves[1].Points.Count);
        Assert.Equal(0, halves[0].Start);
        Assert.Equal(2, halves[0].Count);
        Assert.Equal(2, halves[1].Start);
        Assert.Equal(2, halves[1].Count);
    }

    [Fact]
    public void Splitting_converges_on_the_single_offending_point()
    {
        var points = Enumerable.Range(0, 8).Select(i => Point($"HR:{i}", name: $"T{i}")).ToArray();
        var block = Assert.Single(Plan(points));

        // Keep halving the half that still contains more than one point.
        var current = block;
        var rounds = 0;
        while (true)
        {
            var halves = ModbusReadPlan.Split(current);
            if (halves.Count == 0)
                break;
            current = halves[1];
            rounds++;
        }

        Assert.Single(current.Points);
        Assert.Equal(3, rounds);   // log2(8)
    }

    [Fact]
    public void A_quarantined_block_is_skipped_until_its_time_is_up()
    {
        var block = Assert.Single(Plan(Point("HR:0")));
        var now = DateTimeOffset.UtcNow;

        Assert.False(block.IsQuarantined(now));

        block.Quarantine(now.AddSeconds(60), "refused");
        Assert.True(block.IsQuarantined(now));
        Assert.False(block.IsQuarantined(now.AddSeconds(61)));

        block.RecordSuccess(1.5);
        Assert.False(block.IsQuarantined(now));   // a good read clears it
        Assert.Null(block.ToStat(now).LastError);
    }

    [Fact]
    public void Block_stats_describe_the_request_for_diagnostics()
    {
        var block = Assert.Single(Plan(Point("HR:5"), Point("HR:6", name: "B")));
        block.RecordSuccess(12.5);
        block.RecordError("boom");

        var stat = block.ToStat(DateTimeOffset.UtcNow);

        Assert.Equal("Holding registers", stat.Area);
        Assert.Equal(5, stat.Start);
        Assert.Equal(2, stat.Count);
        Assert.Equal(2, stat.Tags);
        Assert.Equal(1, stat.Reads);
        Assert.Equal(1, stat.Errors);
        Assert.Equal(12.5, stat.LastLatencyMs);
        Assert.Equal("boom", stat.LastError);
    }
}
