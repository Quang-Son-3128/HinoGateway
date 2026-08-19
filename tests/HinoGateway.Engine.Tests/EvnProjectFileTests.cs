using System.Text.Json;
using HinoGateway.Core;
using HinoGateway.Drivers.Modbus;
using HinoGateway.Drivers.SiemensS7;
using HinoGateway.Drivers.Simulator;
using HinoGateway.Server;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// The EVN RIKEN project file that ships in the repository, checked by the same validator the server
/// runs on an apply.
///
/// <para>
/// It is edited by hand — the energy tags were added to it before the tag editor could produce them —
/// and it is the file that gets imported onto the IPC at the plant. A typo in a source key or a folder
/// id costs the utility its daily generation figure and shows up only when somebody looks at IOA 14,
/// so it is worth one test rather than one site visit.
/// </para>
/// </summary>
public class EvnProjectFileTests
{
    private static string ProjectPath()
    {
        // The test binary lives under tests/<project>/bin/<config>/<tfm>; the file is at the root.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "EVNRIKEN_Project.hino");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("EVNRIKEN_Project.hino was not found above the test output directory.");
    }

    private static HinoProject Load() =>
        JsonSerializer.Deserialize<HinoProject>(File.ReadAllText(ProjectPath()),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("the project file did not deserialise.");

    [Fact]
    public void The_shipped_project_has_no_validation_problems()
    {
        // The real factories, so the driver has its say about the addresses too — a DB address the
        // S7 driver cannot parse costs that tag its data at apply time and nothing else says so.
        var problems = new ProjectValidator([
            new SiemensS7DriverFactory(),
            new ModbusTcpDriverFactory(),
            new SimulatorDriverFactory(),
        ]).Validate(Load());

        Assert.Empty(problems);
    }

    /// <summary>
    /// Each inverter reports today, yesterday and this month, all three off the one kWh register.
    /// Getting the source key wrong is the failure this catches: it validates fine against any
    /// existing counter, and only the number is wrong.
    /// </summary>
    [Theory]
    [InlineData("Inv1", "t-inverter-inv1energy")]
    [InlineData("Inv2", "t-inverter-inv2energy")]
    [InlineData("Inv3", "t-inverter-inv3energy")]
    public void Each_inverter_accumulates_over_the_three_periods_from_its_own_counter(
        string inverter, string counterKey)
    {
        var tags = Load().Tags ?? new List<TagDto>();

        foreach (var (suffix, period) in new[]
                 {
                     ("Today", AccumulationPeriod.Day),
                     ("Yesterday", AccumulationPeriod.PreviousDay),
                     ("Month", AccumulationPeriod.Month),
                 })
        {
            var tag = Assert.Single(tags, t => t.Name == $"{inverter}Energy{suffix}");
            Assert.Equal(TagKind.Accumulator, tag.Kind);
            Assert.Equal(period, tag.Period);
            Assert.Equal(counterKey, tag.SourceTagKey);
            Assert.Equal("kWh", tag.Unit);

            // The register is a 32-bit counter read at 0.01 kWh per count. Without this the wrap at
            // 2^32-1 is read as the meter having been replaced and every period is rebased.
            Assert.Equal(42949672.95, tag.RolloverAt, 2);
        }
    }
}
