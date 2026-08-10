using HinoGateway.Core;
using HinoGateway.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

public class AlarmEngineTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private AlarmEngine NewEngine(params AlarmDefinition[] definitions)
    {
        var engine = new AlarmEngine(NullLogger<AlarmEngine>.Instance, () => _now);
        engine.Reload(definitions);
        return engine;
    }

    private static AlarmDefinition Hi(double limit, double hysteresis = 0, int onDelayMs = 0, int offDelayMs = 0,
        bool requiresAck = true, bool enabled = true, string id = "a1") => new()
        {
            Id = id,
            Name = "High",
            TagId = "Area/Temp",
            Condition = AlarmCondition.Hi,
            Limit = limit,
            Hysteresis = hysteresis,
            OnDelayMs = onDelayMs,
            OffDelayMs = offDelayMs,
            RequiresAck = requiresAck,
            Enabled = enabled,
            Severity = AlarmSeverity.Major,
        };

    private void Feed(AlarmEngine engine, object? value, Quality quality = Quality.Good, string tagId = "Area/Temp")
    {
        var sample = quality == Quality.Good
            ? TagValue.Good(value!, _now)
            : new TagValue { Value = value, Quality = quality, SourceTimestamp = _now, LastChanged = _now, StatusText = "test" };
        engine.OnReads([new TagRead(tagId, sample)]);
    }

    private AlarmStatus StatusOf(AlarmEngine engine, string alarmId = "a1") =>
        engine.States(includeNormal: true).Single(a => a.AlarmId == alarmId).Status;

    private static List<AlarmEvent> Drain(AlarmEngine engine)
    {
        var events = new List<AlarmEvent>();
        while (engine.Events.TryRead(out var e))
            events.Add(e);
        return events;
    }

    [Fact]
    public void Level_alarm_activates_on_the_limit_and_needs_hysteresis_to_clear()
    {
        var engine = NewEngine(Hi(limit: 80, hysteresis: 5, requiresAck: false));

        Feed(engine, 79.9);
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine));

        Feed(engine, 80.0); // exactly on the limit counts as met
        Assert.Equal(AlarmStatus.Active, StatusOf(engine));

        Feed(engine, 76.0); // inside the deadband — stays active, no chattering
        Assert.Equal(AlarmStatus.Active, StatusOf(engine));

        Feed(engine, 74.9); // below limit - hysteresis
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine));

        var events = Drain(engine);
        Assert.Equal([AlarmEventType.Activated, AlarmEventType.Cleared], events.Select(e => e.Type));
        Assert.Equal(80.0, events[0].Value);
    }

    [Fact]
    public void On_delay_requires_the_condition_to_persist()
    {
        var engine = NewEngine(Hi(limit: 50, onDelayMs: 2000));

        Feed(engine, 60);
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine)); // pending, not yet active

        _now = _now.AddMilliseconds(1500);
        Feed(engine, 60);
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine));

        _now = _now.AddMilliseconds(500);
        Feed(engine, 60);
        Assert.Equal(AlarmStatus.Active, StatusOf(engine));
    }

    [Fact]
    public void A_transient_excursion_shorter_than_the_on_delay_never_activates()
    {
        var engine = NewEngine(Hi(limit: 50, onDelayMs: 2000));

        Feed(engine, 60);
        _now = _now.AddMilliseconds(1000);
        Feed(engine, 10);              // condition went away — the delay must restart
        _now = _now.AddMilliseconds(1500);
        Feed(engine, 60);
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine));
        Assert.Empty(Drain(engine));
    }

    [Fact]
    public void Off_delay_keeps_the_alarm_active_until_the_condition_has_been_gone_long_enough()
    {
        var engine = NewEngine(Hi(limit: 50, offDelayMs: 1000, requiresAck: false));

        Feed(engine, 60);
        Assert.Equal(AlarmStatus.Active, StatusOf(engine));

        Feed(engine, 10);
        Assert.Equal(AlarmStatus.Active, StatusOf(engine)); // still within the off delay

        _now = _now.AddMilliseconds(1000);
        Feed(engine, 10);
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine));
    }

    [Fact]
    public void Untrusted_quality_freezes_a_level_alarm_instead_of_clearing_it()
    {
        var engine = NewEngine(Hi(limit: 50, requiresAck: false));

        Feed(engine, 60);
        Assert.Equal(AlarmStatus.Active, StatusOf(engine));

        // A Bad sample says nothing about the process value: the alarm must not be cleared by it.
        Feed(engine, null, Quality.Bad);
        Assert.Equal(AlarmStatus.Active, StatusOf(engine));

        Feed(engine, 10);
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine));
    }

    [Fact]
    public void Bad_quality_alarm_reports_comms_loss_and_recovers()
    {
        var engine = NewEngine(new AlarmDefinition
        {
            Id = "q1", Name = "Comms", TagId = "Area/Temp",
            Condition = AlarmCondition.BadQuality, RequiresAck = false, Severity = AlarmSeverity.Critical,
        });

        Feed(engine, 42);
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine, "q1"));

        Feed(engine, null, Quality.Bad);
        Assert.Equal(AlarmStatus.Active, StatusOf(engine, "q1"));

        Feed(engine, 42);
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine, "q1"));
    }

    [Theory]
    [InlineData(true, true, AlarmStatus.Active)]
    [InlineData(true, false, AlarmStatus.Normal)]
    [InlineData(false, false, AlarmStatus.Active)]
    [InlineData(false, true, AlarmStatus.Normal)]
    public void Digital_alarm_honours_the_configured_trigger_edge(bool triggerOnTrue, bool value, AlarmStatus expected)
    {
        var engine = NewEngine(new AlarmDefinition
        {
            Id = "d1", Name = "Pump fault", TagId = "Area/Temp",
            Condition = AlarmCondition.Digital, TriggerOnTrue = triggerOnTrue, RequiresAck = false,
        });

        Feed(engine, value);
        Assert.Equal(expected, StatusOf(engine, "d1"));
    }

    [Fact]
    public void Acknowledging_an_active_alarm_keeps_it_listed_until_it_clears()
    {
        var engine = NewEngine(Hi(limit: 50));

        Feed(engine, 60);
        Assert.Equal(AlarmStatus.Active, StatusOf(engine));

        Assert.True(engine.Acknowledge("a1", "sonng"));
        Assert.Equal(AlarmStatus.ActiveAcknowledged, StatusOf(engine));
        Assert.False(engine.Acknowledge("a1", "sonng")); // already acknowledged

        Feed(engine, 10);
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine));

        var state = engine.States(includeNormal: true).Single();
        Assert.Equal("sonng", state.AckedBy);
        Assert.Equal([AlarmEventType.Activated, AlarmEventType.Acknowledged, AlarmEventType.Cleared],
            Drain(engine).Select(e => e.Type));
    }

    [Fact]
    public void An_alarm_that_requires_ack_latches_after_it_clears()
    {
        var engine = NewEngine(Hi(limit: 50));

        Feed(engine, 60);
        Feed(engine, 10);
        Assert.Equal(AlarmStatus.ClearedUnacknowledged, StatusOf(engine));
        Assert.Single(engine.States()); // still shown to the operator

        Assert.True(engine.Acknowledge("a1", null));
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine));
        Assert.Empty(engine.States());
    }

    [Fact]
    public void An_alarm_without_ack_requirement_disappears_as_soon_as_it_clears()
    {
        var engine = NewEngine(Hi(limit: 50, requiresAck: false));

        Feed(engine, 60);
        Feed(engine, 10);
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine));
        Assert.Empty(engine.States());
    }

    [Fact]
    public void Disabled_alarms_are_never_evaluated()
    {
        var engine = NewEngine(Hi(limit: 50, enabled: false));

        Feed(engine, 999);
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine));
        Assert.Empty(Drain(engine));
    }

    [Fact]
    public void Reload_keeps_the_state_of_an_outstanding_alarm_but_applies_the_new_limit()
    {
        var engine = NewEngine(Hi(limit: 50));

        Feed(engine, 60);
        engine.Acknowledge("a1", "op");
        var before = engine.States().Single();

        engine.Reload([Hi(limit: 90)]); // same alarm, tighter limit
        var after = engine.States().Single();

        Assert.Equal(AlarmStatus.ActiveAcknowledged, after.Status);
        Assert.Equal(before.ActiveSince, after.ActiveSince);
        Assert.Equal(1, after.ActivationCount);
        Assert.Equal(90, after.Limit);

        Feed(engine, 60); // below the new limit — the alarm clears
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine));
    }

    [Fact]
    public void Reload_starts_fresh_when_the_alarm_is_repointed_at_another_tag()
    {
        var engine = NewEngine(Hi(limit: 50));
        Feed(engine, 60);
        Assert.Equal(AlarmStatus.Active, StatusOf(engine));

        engine.Reload([Hi(limit: 50) with { TagId = "Area/Other" }]);
        Assert.Equal(AlarmStatus.Normal, StatusOf(engine));
    }

    [Fact]
    public void Summary_counts_active_and_outstanding_alarms_by_severity()
    {
        var engine = NewEngine(
            Hi(limit: 50, id: "a1"),
            new AlarmDefinition
            {
                Id = "a2", Name = "Critical high", TagId = "Area/Temp",
                Condition = AlarmCondition.HiHi, Limit = 90, Severity = AlarmSeverity.Critical,
            });

        Feed(engine, 95);
        var summary = engine.Summary();

        Assert.Equal(2, summary.Configured);
        Assert.Equal(2, summary.Active);
        Assert.Equal(2, summary.Unacknowledged);
        Assert.Equal(AlarmSeverity.Critical, summary.HighestActiveSeverity);
        Assert.Equal(AlarmSeverity.Critical, summary.ActiveBySeverity[0].Severity);

        Assert.Equal(2, engine.AcknowledgeAll("op"));
        Assert.Equal(0, engine.Summary().Unacknowledged);
        Assert.Equal(2, engine.Summary().Active); // acknowledged, but still active
    }

    [Fact]
    public void Alarms_are_evaluated_through_the_tag_store_observer_hook()
    {
        var store = new TagStore();
        var engine = NewEngine(Hi(limit: 50, requiresAck: false));
        store.AddObserver(engine.OnReads);

        store.Apply([new TagRead("Area/Temp", TagValue.Good(60.0, _now))]);

        Assert.Equal(AlarmStatus.Active, StatusOf(engine));
        Assert.Equal(0, store.ObserverErrors);
    }
}
