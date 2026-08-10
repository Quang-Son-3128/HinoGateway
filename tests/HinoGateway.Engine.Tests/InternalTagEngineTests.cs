using HinoGateway.Core;
using HinoGateway.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// The producer behind memory and expression tags. What is tested here is mostly about time and
/// order: that a chain of derived values settles inside one pass, that a configuration apply does not
/// wipe out what an operator wrote, and that a formula which cannot work costs exactly one tag.
/// </summary>
public class InternalTagEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 10, 8, 0, 0, TimeSpan.FromHours(7));

    private static TagDefinition Expression(string id, string formula,
        EvalMode mode = EvalMode.Scan, DataType type = DataType.Float64, TimeSpan? every = null) => new()
    {
        Id = id,
        Name = id,
        Kind = TagKind.Expression,
        Address = "",
        Expression = formula,
        EvalMode = mode,
        DataType = type,
        ScanInterval = every ?? TimeSpan.FromMilliseconds(50),
    };

    private static TagDefinition Memory(string id, string? initial = null,
        bool retentive = false, DataType type = DataType.Float64) => new()
    {
        Id = id,
        Name = id,
        Kind = TagKind.Memory,
        Address = "",
        InitialValue = initial,
        Retentive = retentive,
        DataType = type,
    };

    private static InternalTagEngine Engine(TagStore store, IRetainedValueStore? retained = null) =>
        new(store, NullLogger.Instance, retained);

    private static async Task<TagValue> WaitFor(TagStore store, string tagId, Func<TagValue, bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (store.TryGet(tagId, out var value) && until(value))
                return value;
            await Task.Delay(10);
        }

        store.TryGet(tagId, out var last);
        throw new Xunit.Sdk.XunitException(
            $"'{tagId}' never reached the expected state (last: {last?.Value ?? "none"} / {last?.Quality} / {last?.StatusText})");
    }

    // --- evaluation -----------------------------------------------------------

    [Fact]
    public async Task An_expression_tag_computes_from_a_device_tag()
    {
        var store = new TagStore();
        store.Apply([new TagRead("Plant/P1", TagValue.Good(100.0, T0))]);

        await using var engine = Engine(store);
        engine.Load([Expression("Calc/Total", "{Plant/P1} * 2")]);
        engine.Start();

        var value = await WaitFor(store, "Calc/Total", v => v.Quality == Quality.Good);
        Assert.Equal(200.0, value.Value);
    }

    /// <summary>
    /// Evaluation order is the reason this engine sorts its tags. Both derived values carry the same
    /// source timestamp because they were computed in the same pass — had C been evaluated against
    /// the previous pass's B, it would lag one tick behind for ever.
    /// </summary>
    [Fact]
    public async Task A_chain_of_expressions_settles_within_one_pass()
    {
        var store = new TagStore();
        store.Apply([new TagRead("A", TagValue.Good(10.0, T0))]);

        await using var engine = Engine(store);
        // Deliberately declared in the wrong order: C before the B it depends on.
        engine.Load([Expression("C", "{B} + 1"), Expression("B", "{A} * 2")]);
        engine.Start();

        var c = await WaitFor(store, "C", v => v.Quality == Quality.Good);
        Assert.True(store.TryGet("B", out var b));
        Assert.Equal(20.0, b.Value);
        Assert.Equal(21.0, c.Value);
        Assert.Equal(b.SourceTimestamp, c.SourceTimestamp);
    }

    [Fact]
    public async Task A_bad_source_makes_the_computed_tag_bad_rather_than_wrong()
    {
        var store = new TagStore();
        store.Apply([new TagRead("P1", TagValue.Bad("timeout", T0)), new TagRead("P2", TagValue.Good(50.0, T0))]);

        await using var engine = Engine(store);
        engine.Load([Expression("Total", "{P1} + {P2}")]);
        engine.Start();

        var value = await WaitFor(store, "Total", v => v.Quality == Quality.Bad && v.StatusText is not null);
        Assert.Contains("P1", value.StatusText);
    }

    [Fact]
    public async Task An_on_change_tag_recomputes_when_its_input_moves()
    {
        var store = new TagStore();
        store.Apply([new TagRead("A", TagValue.Good(1.0, T0))]);

        await using var engine = Engine(store);
        engine.Load([Expression("Doubled", "{A} * 2", EvalMode.OnChange, every: TimeSpan.FromHours(1))]);
        engine.Start();

        store.Apply([new TagRead("A", TagValue.Good(21.0, T0.AddSeconds(1)))]);

        var value = await WaitFor(store, "Doubled", v => Equals(v.Value, 42.0));
        Assert.Equal(Quality.Good, value.Quality);
    }

    // --- failure isolation ----------------------------------------------------

    [Fact]
    public async Task A_formula_that_does_not_compile_costs_only_its_own_tag()
    {
        var store = new TagStore();
        store.Apply([new TagRead("A", TagValue.Good(5.0, T0))]);

        await using var engine = Engine(store);
        engine.Load([Expression("Broken", "{A} +"), Expression("Fine", "{A} + 1")]);

        Assert.True(store.TryGet("Broken", out var broken));
        Assert.Equal(Quality.Bad, broken.Quality);
        Assert.Single(engine.Faults);
        Assert.Equal("Broken", engine.Faults[0].TagId);
        Assert.Equal(1, engine.Stats.FaultedTags);
        Assert.Equal(2, engine.Stats.ExpressionTags);
    }

    [Fact]
    public async Task A_circular_reference_is_refused_and_both_tags_say_why()
    {
        var store = new TagStore();

        await using var engine = Engine(store);
        engine.Load([Expression("A", "{B} + 1"), Expression("B", "{A} + 1")]);

        Assert.Equal(2, engine.Faults.Count);
        foreach (var tag in new[] { "A", "B" })
        {
            Assert.True(store.TryGet(tag, out var value));
            Assert.Equal(Quality.Bad, value.Quality);
            Assert.Contains("circular", value.StatusText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_result_that_does_not_fit_the_tag_type_is_refused_with_the_range()
    {
        var store = new TagStore();

        await using var engine = Engine(store);
        engine.Load([Expression("Big", "40000", type: DataType.Int16)]);
        engine.Start();

        var value = await WaitFor(store, "Big", v => v.Quality == Quality.Bad);
        Assert.Contains("Int16", value.StatusText);
    }

    // --- memory tags ----------------------------------------------------------

    [Fact]
    public async Task A_memory_tag_starts_from_its_initial_value_and_holds_what_is_written()
    {
        var store = new TagStore();

        await using var engine = Engine(store);
        engine.Load([Memory("Setpoint", "20")]);

        Assert.True(store.TryGet("Setpoint", out var initial));
        Assert.Equal(20.0, initial.Value);

        await engine.WriteAsync("Setpoint", 23.5);
        Assert.True(store.TryGet("Setpoint", out var written));
        Assert.Equal(23.5, written.Value);
        Assert.Equal(Quality.Good, written.Quality);
    }

    /// <summary>
    /// A setpoint nobody has set has no value. Serving 0 for it would be indistinguishable from
    /// someone having chosen 0, which is the kind of difference that matters on a control point.
    /// </summary>
    [Fact]
    public async Task A_memory_tag_with_no_initial_value_starts_bad()
    {
        var store = new TagStore();

        await using var engine = Engine(store);
        engine.Load([Memory("Setpoint")]);

        Assert.True(store.TryGet("Setpoint", out var value));
        Assert.Equal(Quality.Bad, value.Quality);
        Assert.Equal("never written", value.StatusText);
    }

    /// <summary>
    /// Pressing "Apply to engine" rebuilds the runtime. Without seeding from the store first, every
    /// setpoint in the plant would silently jump back to its configured initial value.
    /// </summary>
    [Fact]
    public async Task Applying_a_configuration_does_not_reset_a_memory_tag()
    {
        var store = new TagStore();

        var first = Engine(store);
        first.Load([Memory("Setpoint", "20")]);
        await first.WriteAsync("Setpoint", 75.0);
        await first.DisposeAsync();

        await using var second = Engine(store);
        second.Load([Memory("Setpoint", "20")]);

        Assert.True(store.TryGet("Setpoint", out var value));
        Assert.Equal(75.0, value.Value);
    }

    [Fact]
    public async Task Only_a_retentive_memory_tag_comes_back_after_a_restart()
    {
        var retained = new FakeRetainedStore();

        var before = Engine(new TagStore(), retained);
        before.Load([Memory("Kept", "1", retentive: true), Memory("Lost", "1")]);
        await before.WriteAsync("Kept", 42.0);
        await before.WriteAsync("Lost", 42.0);
        await before.DisposeAsync();

        // A restart: a brand new tag store, so nothing survives except through the sidecar.
        var store = new TagStore();
        await using var after = Engine(store, retained);
        after.Load([Memory("Kept", "1", retentive: true), Memory("Lost", "1")]);

        Assert.True(store.TryGet("Kept", out var kept));
        Assert.Equal(42.0, kept.Value);
        Assert.True(store.TryGet("Lost", out var lost));
        Assert.Equal(1.0, lost.Value);
    }

    [Fact]
    public async Task Turning_retention_off_forgets_the_stored_value()
    {
        var retained = new FakeRetainedStore();
        retained.Save("Setpoint", 42.0);

        await using var engine = Engine(new TagStore(), retained);
        engine.Load([Memory("Setpoint", "1")]);   // no longer retentive

        Assert.False(retained.TryGet("Setpoint", out _));
    }

    [Fact]
    public async Task A_write_is_coerced_to_the_tag_type_and_refused_when_it_cannot_be()
    {
        var store = new TagStore();

        await using var engine = Engine(store);
        engine.Load([Memory("Count", "0", type: DataType.Int16), Memory("Mode", "false", type: DataType.Bool)]);

        await engine.WriteAsync("Count", 12.7);
        Assert.True(store.TryGet("Count", out var count));
        Assert.Equal((short)13, count.Value);

        await engine.WriteAsync("Mode", "on");
        Assert.True(store.TryGet("Mode", out var mode));
        Assert.Equal(true, mode.Value);

        await Assert.ThrowsAsync<ArgumentException>(() => engine.WriteAsync("Count", 40000.0));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.WriteAsync("Count", "not a number"));
    }

    [Fact]
    public async Task An_expression_tag_cannot_be_written_to()
    {
        var store = new TagStore();

        await using var engine = Engine(store);
        engine.Load([Expression("Total", "1 + 1")]);

        await Assert.ThrowsAsync<NotSupportedException>(() => engine.WriteAsync("Total", 5.0));
    }

    // --- wiring into the device engine ---------------------------------------

    [Fact]
    public async Task The_device_engine_routes_writes_and_keeps_internal_tags_across_a_reload()
    {
        var store = new TagStore();
        await using var engine = new DeviceEngine(
            Array.Empty<IDeviceDriverFactory>(), store, NullLoggerFactory.Instance);

        var tags = new[] { Memory("Plant/Setpoint", "10") };
        await engine.LoadAsync(Array.Empty<DeviceConfig>(), tags);
        engine.StartAll();

        await engine.WriteTagAsync("Plant/Setpoint", 55.0);
        Assert.True(store.TryGet("Plant/Setpoint", out var written));
        Assert.Equal(55.0, written.Value);

        // RetainOnly() prunes tags that are no longer configured; an internal tag must count as
        // configured or every reload would delete it from the store.
        await engine.ReloadAsync(Array.Empty<DeviceConfig>(), tags);
        Assert.True(store.TryGet("Plant/Setpoint", out var afterReload));
        Assert.Equal(55.0, afterReload.Value);
        Assert.Equal(1, engine.InternalTags.MemoryTags);
    }

    [Fact]
    public async Task A_write_to_an_unknown_tag_is_still_reported_as_missing()
    {
        var store = new TagStore();
        await using var engine = new DeviceEngine(
            Array.Empty<IDeviceDriverFactory>(), store, NullLoggerFactory.Instance);
        await engine.LoadAsync(Array.Empty<DeviceConfig>(), [Memory("Known")]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => engine.WriteTagAsync("Unknown", 1.0));
    }

    private sealed class FakeRetainedStore : IRetainedValueStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public bool TryGet(string tagId, out object? value) => _values.TryGetValue(tagId, out value);

        public void Save(string tagId, object? value) => _values[tagId] = value;

        public void RetainOnly(IEnumerable<string> tagIds)
        {
            var keep = new HashSet<string>(tagIds, StringComparer.Ordinal);
            foreach (var key in _values.Keys.ToList())
                if (!keep.Contains(key))
                    _values.Remove(key);
        }
    }
}
