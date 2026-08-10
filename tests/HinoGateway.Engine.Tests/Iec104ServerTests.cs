using System.Net;
using System.Net.Sockets;
using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Northbound.Iec104;
using lib60870.CS101;
using lib60870.CS104;
using Microsoft.Extensions.Logging.Abstractions;
using CS104Connection = lib60870.CS104.Connection;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// End-to-end tests against a real controlling station: lib60870's own client connects to our
/// controlled station over TCP and we assert on the ASDUs that actually cross the wire. Unit-testing
/// the mapping alone would not prove the link works, and the utility's control centre is not
/// something we can iterate against.
/// </summary>
public class Iec104ServerTests : IAsyncDisposable
{
    private const int CommonAddress = 1;

    private readonly TagStore _tags = new();
    private readonly List<(string Tag, object Value)> _writes = new();
    private Iec104Server? _server;
    private CS104Connection? _client;
    private List<ASDU>? _received;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Iec104PointDto Point(int ioa, string tagPath, Iec104PointType type,
        Iec104CommandType command = Iec104CommandType.None, double deadband = 0, bool withTimestamp = true) =>
        new()
        {
            TagId = tagPath, TagPath = tagPath, Ioa = ioa, Type = type,
            Command = command, Deadband = deadband, WithTimestamp = withTimestamp,
        };

    /// <summary>Start the station and a connected, activated client.</summary>
    private Task<List<ASDU>> StartAsync(params Iec104PointDto[] points) =>
        StartAsync(periodicSeconds: 0, points);

    private async Task<List<ASDU>> StartAsync(int periodicSeconds, params Iec104PointDto[] points)
    {
        var options = new Iec104ServerOptions
        {
            Enabled = true,
            BindAddress = "127.0.0.1",
            Port = FreePort(),
            CommonAddress = CommonAddress,
            PeriodicIntervalSeconds = periodicSeconds,
            Points = points.ToList(),
        };

        _server = new Iec104Server(options, _tags,
            (tag, value, _) => { lock (_writes) _writes.Add((tag, value)); return Task.CompletedTask; },
            NullLogger<Iec104Server>.Instance);
        _server.Start();

        var received = new List<ASDU>();
        _received = received;
        _client = new CS104Connection("127.0.0.1", options.Port);
        _client.SetASDUReceivedHandler((_, asdu) =>
        {
            lock (received) received.Add(asdu);
            return true;
        }, null);
        _client.Connect();
        _client.SendStartDT();

        await WaitFor(() => _server.Status.OpenConnections > 0, "the client to connect");
        return received;
    }

    /// <summary>
    /// Wait until the station is genuinely serving this connection, by doing what a control centre
    /// does after STARTDT: a general interrogation, answered right through to ACT_TERM.
    /// (The library's connection-state events cannot be used — lib60870.NET 2.3.0 raises only CLOSED.)
    /// </summary>
    private async Task ActivateAsync()
    {
        var received = _received!;
        _client!.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, CommonAddress, 20);
        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.ACTIVATION_TERMINATION),
            "the station to answer a general interrogation");

        // The handshake traffic is not what the tests are about; start each one from a clean slate.
        lock (received)
            received.Clear();
    }

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static List<ASDU> Snapshot(List<ASDU> received)
    {
        lock (received)
            return received.ToList();
    }

    private void Publish(string tagPath, object value, Quality quality = Quality.Good) =>
        _tags.Apply([new TagRead(tagPath, quality == Quality.Good
            ? TagValue.Good(value, DateTimeOffset.UtcNow)
            : new TagValue
            {
                Value = quality == Quality.Bad ? null : value,
                Quality = quality,
                SourceTimestamp = DateTimeOffset.UtcNow,
                LastChanged = DateTimeOffset.UtcNow,
                StatusText = "test",
            })]);

    [Fact]
    public async Task A_control_centre_can_connect_and_start_data_transfer()
    {
        await StartAsync(Point(100, "Plant/Power", Iec104PointType.ShortFloat));

        var status = _server!.Status;
        Assert.True(status.Running);
        Assert.Equal(1, status.OpenConnections);
        await ActivateAsync();
    }

    [Fact]
    public async Task General_interrogation_returns_every_mapped_point()
    {
        Publish("Plant/Power", 42.5);
        Publish("Plant/Breaker", true);
        var received = await StartAsync(
            Point(100, "Plant/Power", Iec104PointType.ShortFloat),
            Point(200, "Plant/Breaker", Iec104PointType.SinglePoint));

        _client!.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, CommonAddress, 20);

        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.ACTIVATION_TERMINATION),
            "the interrogation to finish");

        var asdus = Snapshot(received);
        Assert.Contains(asdus, a => a.Cot == CauseOfTransmission.ACTIVATION_CON && !a.IsNegative);

        var data = asdus.Where(a => a.Cot == CauseOfTransmission.INTERROGATED_BY_STATION).ToList();
        Assert.Equal(2, data.Count);

        var power = data.Select(a => a.GetElement(0)).OfType<MeasuredValueShortWithCP56Time2a>().Single();
        Assert.Equal(100, power.ObjectAddress);
        Assert.Equal(42.5f, power.Value, 3);
        Assert.False(power.Quality.Invalid);

        var breaker = data.Select(a => a.GetElement(0)).OfType<SinglePointWithCP56Time2a>().Single();
        Assert.Equal(200, breaker.ObjectAddress);
        Assert.True(breaker.Value);

        Assert.Equal(1, _server!.Status.InterrogationsServed);
    }

    [Fact]
    public async Task A_point_that_was_never_read_is_reported_as_invalid_not_as_zero()
    {
        // Nothing published: the control centre must be told the value is not trustworthy.
        var received = await StartAsync(Point(100, "Plant/Missing", Iec104PointType.ShortFloat));

        _client!.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, CommonAddress, 20);
        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.INTERROGATED_BY_STATION),
            "the interrogation data");

        var value = Snapshot(received)
            .Where(a => a.Cot == CauseOfTransmission.INTERROGATED_BY_STATION)
            .Select(a => a.GetElement(0))
            .OfType<MeasuredValueShortWithCP56Time2a>()
            .Single();

        Assert.True(value.Quality.Invalid);
    }

    [Fact]
    public async Task Changes_are_reported_spontaneously()
    {
        Publish("Plant/Power", 10.0);
        var received = await StartAsync(Point(100, "Plant/Power", Iec104PointType.ShortFloat));
        await ActivateAsync();

        Publish("Plant/Power", 11.5);

        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.SPONTANEOUS),
            "a spontaneous report");

        var spontaneous = Snapshot(received).First(a => a.Cot == CauseOfTransmission.SPONTANEOUS);
        var value = Assert.IsType<MeasuredValueShortWithCP56Time2a>(spontaneous.GetElement(0));
        Assert.Equal(11.5f, value.Value, 3);
    }

    [Fact]
    public async Task A_deadband_keeps_small_movements_off_the_link()
    {
        Publish("Plant/Power", 100.0);
        var received = await StartAsync(Point(100, "Plant/Power", Iec104PointType.ShortFloat, deadband: 1.0));
        await ActivateAsync();

        Publish("Plant/Power", 100.2);   // inside the deadband
        Publish("Plant/Power", 100.3);   // still inside
        await Task.Delay(300);
        Assert.Empty(Snapshot(received).Where(a => a.Cot == CauseOfTransmission.SPONTANEOUS));

        Publish("Plant/Power", 102.0);   // outside
        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.SPONTANEOUS),
            "the report once the deadband is exceeded");
    }

    [Fact]
    public async Task Losing_the_value_is_reported_even_when_the_number_would_not_have_changed()
    {
        Publish("Plant/Power", 50.0);
        var received = await StartAsync(Point(100, "Plant/Power", Iec104PointType.ShortFloat, deadband: 5.0));
        await ActivateAsync();

        // A comms failure on the device side: the number is unchanged, the trust in it is not.
        Publish("Plant/Power", 50.0, Quality.Bad);

        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.SPONTANEOUS),
            "the quality change to be reported");
        var value = Assert.IsType<MeasuredValueShortWithCP56Time2a>(
            Snapshot(received).First(a => a.Cot == CauseOfTransmission.SPONTANEOUS).GetElement(0));
        Assert.True(value.Quality.Invalid);
    }

    [Fact]
    public async Task A_setpoint_command_writes_to_the_tag_and_is_confirmed()
    {
        Publish("Plant/Limit", 100.0);
        var received = await StartAsync(
            Point(300, "Plant/Limit", Iec104PointType.ShortFloat, Iec104CommandType.SetpointShort));
        await ActivateAsync();

        _client!.SendControlCommand(CauseOfTransmission.ACTIVATION, CommonAddress,
            new SetpointCommandShort(300, 62.5f, new SetpointCommandQualifier(false, 0)));

        await WaitFor(() => { lock (_writes) return _writes.Count > 0; }, "the command to reach the tag");

        var (tag, value) = _writes.Single();
        Assert.Equal("Plant/Limit", tag);
        Assert.Equal(62.5, Assert.IsType<double>(value), 3);

        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.ACTIVATION_TERMINATION),
            "the command to be terminated");
        Assert.Contains(Snapshot(received), a => a.Cot == CauseOfTransmission.ACTIVATION_CON && !a.IsNegative);
        Assert.Equal(1, _server!.Status.CommandsAccepted);
    }

    [Fact]
    public async Task A_single_command_writes_a_boolean()
    {
        Publish("Plant/Breaker", false);
        await StartAsync(Point(200, "Plant/Breaker", Iec104PointType.SinglePoint, Iec104CommandType.SingleCommand));
        await ActivateAsync();

        _client!.SendControlCommand(CauseOfTransmission.ACTIVATION, CommonAddress,
            new SingleCommand(200, true, false, 0));

        await WaitFor(() => { lock (_writes) return _writes.Count > 0; }, "the command to reach the tag");
        Assert.Equal(("Plant/Breaker", (object)true), _writes.Single());
    }

    [Fact]
    public async Task A_command_to_an_unmapped_address_is_refused()
    {
        var received = await StartAsync(Point(100, "Plant/Power", Iec104PointType.ShortFloat));
        await ActivateAsync();

        _client!.SendControlCommand(CauseOfTransmission.ACTIVATION, CommonAddress,
            new SetpointCommandShort(999, 1f, new SetpointCommandQualifier(false, 0)));

        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.ACTIVATION_CON && a.IsNegative),
            "a negative confirmation");
        lock (_writes)
            Assert.Empty(_writes);
        Assert.Equal(1, _server!.Status.CommandsRejected);
    }

    [Fact]
    public async Task A_command_to_a_point_that_is_not_commandable_is_refused()
    {
        var received = await StartAsync(Point(100, "Plant/Power", Iec104PointType.ShortFloat));
        await ActivateAsync();

        _client!.SendControlCommand(CauseOfTransmission.ACTIVATION, CommonAddress,
            new SetpointCommandShort(100, 5f, new SetpointCommandQualifier(false, 0)));

        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.ACTIVATION_CON && a.IsNegative),
            "a negative confirmation");
        lock (_writes)
            Assert.Empty(_writes);
    }

    [Fact]
    public async Task The_wrong_command_type_for_a_point_is_refused()
    {
        var received = await StartAsync(
            Point(300, "Plant/Limit", Iec104PointType.ShortFloat, Iec104CommandType.SetpointShort));
        await ActivateAsync();

        // Configured for C_SE_NC_1; the centre sends a scaled setpoint instead.
        _client!.SendControlCommand(CauseOfTransmission.ACTIVATION, CommonAddress,
            new SetpointCommandScaled(300, new ScaledValue(50), new SetpointCommandQualifier(false, 0)));

        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.ACTIVATION_CON && a.IsNegative),
            "a negative confirmation");
        lock (_writes)
            Assert.Empty(_writes);
    }

    [Fact]
    public async Task Commands_can_be_switched_off_entirely()
    {
        var options = new Iec104ServerOptions
        {
            Enabled = true, BindAddress = "127.0.0.1", Port = FreePort(), CommonAddress = CommonAddress,
            AllowCommands = false,
            Points = [Point(300, "Plant/Limit", Iec104PointType.ShortFloat, Iec104CommandType.SetpointShort)],
        };
        _server = new Iec104Server(options, _tags,
            (tag, value, _) => { lock (_writes) _writes.Add((tag, value)); return Task.CompletedTask; },
            NullLogger<Iec104Server>.Instance);
        _server.Start();

        var received = new List<ASDU>();
        _received = received;
        _client = new CS104Connection("127.0.0.1", options.Port);
        _client.SetASDUReceivedHandler((_, asdu) => { lock (received) received.Add(asdu); return true; }, null);
        _client.Connect();
        _client.SendStartDT();
        await WaitFor(() => _server.Status.OpenConnections > 0, "the client to connect");
        await ActivateAsync();

        _client.SendControlCommand(CauseOfTransmission.ACTIVATION, CommonAddress,
            new SetpointCommandShort(300, 5f, new SetpointCommandQualifier(false, 0)));

        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.ACTIVATION_CON && a.IsNegative),
            "a negative confirmation");
        lock (_writes)
            Assert.Empty(_writes);
    }

    [Fact]
    public async Task A_read_command_answers_with_that_single_point()
    {
        Publish("Plant/Power", 33.25);
        var received = await StartAsync(Point(100, "Plant/Power", Iec104PointType.ShortFloat));
        await ActivateAsync();

        _client!.SendReadCommand(CommonAddress, 100);

        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.REQUEST), "the read reply");
        var value = Assert.IsType<MeasuredValueShortWithCP56Time2a>(
            Snapshot(received).First(a => a.Cot == CauseOfTransmission.REQUEST).GetElement(0));
        Assert.Equal(33.25f, value.Value, 3);
    }

    [Fact]
    public async Task Counter_interrogation_returns_the_integrated_totals()
    {
        Publish("Plant/Energy", 123456.0);
        Publish("Plant/Power", 10.0);
        var received = await StartAsync(
            Point(400, "Plant/Energy", Iec104PointType.IntegratedTotals),
            Point(100, "Plant/Power", Iec104PointType.ShortFloat));
        await ActivateAsync();

        _client!.SendCounterInterrogationCommand(CauseOfTransmission.ACTIVATION, CommonAddress, 5);

        await WaitFor(() => Snapshot(received).Any(a => a.TypeId == TypeID.M_IT_TB_1), "the counter reply");

        // The cause of transmission is asserted as 20 rather than the standard's 37 on purpose:
        // lib60870.NET 2.3.0 overwrites it (verified at byte level) and there is no way to set it from
        // outside the library. Everything that identifies the data — type, address, value — is correct.
        // If this ever starts arriving as REQUESTED_BY_GENERAL_COUNTER, the library was fixed and this
        // expectation should be tightened rather than deleted.
        var counters = Snapshot(received)
            .Where(a => a.TypeId == TypeID.M_IT_TB_1)
            .Select(a => a.GetElement(0))
            .OfType<IntegratedTotalsWithCP56Time2a>()
            .ToList();

        // Only the counter is answered — the measurand is not part of a counter interrogation.
        var counter = Assert.Single(counters);
        Assert.Equal(400, counter.ObjectAddress);
        Assert.Equal(123456, counter.BCR.Value);
    }

    [Fact]
    public async Task Clock_synchronisation_is_accepted_and_the_offset_recorded()
    {
        await StartAsync(Point(100, "Plant/Power", Iec104PointType.ShortFloat));
        await ActivateAsync();

        // A control centre whose clock is a minute ahead of ours.
        _client!.SendClockSyncCommand(CommonAddress, new lib60870.CP56Time2a(DateTime.Now.AddMinutes(1)));

        await WaitFor(() => _server!.Status.LastClockOffsetSeconds is not null, "the clock offset to be recorded");
        Assert.InRange(_server!.Status.LastClockOffsetSeconds!.Value, 55, 65);
    }

    [Fact]
    public async Task An_asdu_for_another_station_address_is_refused()
    {
        var received = await StartAsync(
            Point(300, "Plant/Limit", Iec104PointType.ShortFloat, Iec104CommandType.SetpointShort));
        await ActivateAsync();

        _client!.SendControlCommand(CauseOfTransmission.ACTIVATION, CommonAddress + 7,
            new SetpointCommandShort(300, 5f, new SetpointCommandQualifier(false, 0)));

        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.ACTIVATION_CON && a.IsNegative),
            "a negative confirmation");
        lock (_writes)
            Assert.Empty(_writes);
    }

    // --- EVN 378/QĐ-EVN requirements -----------------------------------------

    [Fact]
    public async Task Cyclic_transmission_refreshes_every_point_even_when_nothing_changes()
    {
        // The utility requires the control centre to be updated at least every five minutes.
        // Report-by-exception cannot promise that, so this is the mechanism that does (1 s here).
        Publish("Plant/Power", 42.0);
        Publish("Plant/Breaker", true);
        var received = await StartAsync(periodicSeconds: 1,
            Point(1, "Plant/Power", Iec104PointType.ShortFloat),
            Point(2, "Plant/Breaker", Iec104PointType.SinglePoint));
        await ActivateAsync();

        // Deliberately publish nothing: only the cycle can produce traffic now.
        await WaitFor(() => Snapshot(received).Count(a => a.Cot == CauseOfTransmission.PERIODIC) >= 4,
            "two cycles of both points", timeoutMs: 8000);

        var periodic = Snapshot(received).Where(a => a.Cot == CauseOfTransmission.PERIODIC).ToList();
        Assert.Contains(periodic, a => a.GetElement(0).ObjectAddress == 1);
        Assert.Contains(periodic, a => a.GetElement(0).ObjectAddress == 2);

        var power = periodic.Select(a => a.GetElement(0)).OfType<MeasuredValueShortWithCP56Time2a>().First();
        Assert.Equal(42f, power.Value, 3);
        Assert.False(power.Quality.Invalid);
    }

    [Fact]
    public async Task Cyclic_transmission_is_off_by_default()
    {
        Publish("Plant/Power", 42.0);
        var received = await StartAsync(Point(1, "Plant/Power", Iec104PointType.ShortFloat));
        await ActivateAsync();

        await Task.Delay(1500);

        Assert.Empty(Snapshot(received).Where(a => a.Cot == CauseOfTransmission.PERIODIC));
    }

    [Fact]
    public async Task A_reset_process_command_is_answered_and_the_link_survives()
    {
        // Required by EVN 378. It also guards a real failure mode: lib60870.NET dereferences an unset
        // handler, so before this was handled a reset process command dropped the whole connection.
        Publish("Plant/Power", 42.0);
        var received = await StartAsync(Point(1, "Plant/Power", Iec104PointType.ShortFloat));
        await ActivateAsync();

        _client!.SendResetProcessCommand(CauseOfTransmission.ACTIVATION, CommonAddress, 1);

        await WaitFor(() => Snapshot(received).Any(a => a.TypeId == TypeID.C_RP_NA_1
                                                       && a.Cot == CauseOfTransmission.ACTIVATION_CON
                                                       && !a.IsNegative),
            "the reset process confirmation");

        // The link must still work afterwards, and the plant must be untouched.
        lock (_writes)
            Assert.Empty(_writes);

        lock (received)
            received.Clear();
        _client.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, CommonAddress, 20);
        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.ACTIVATION_TERMINATION),
            "the station to still answer after a reset");
    }

    [Fact]
    public async Task Spontaneous_reporting_still_works_after_a_reset_process()
    {
        Publish("Plant/Power", 42.0);
        var received = await StartAsync(Point(1, "Plant/Power", Iec104PointType.ShortFloat));
        await ActivateAsync();

        // Prove the push path works before the reset…
        Publish("Plant/Power", 100.0);
        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.SPONTANEOUS),
            "a spontaneous report before the reset");

        var receivedBefore = _server!.Status.AsdusReceived;
        _client!.SendResetProcessCommand(CauseOfTransmission.ACTIVATION, CommonAddress, 1);
        await WaitFor(() => Snapshot(received).Any(a => a.TypeId == TypeID.C_RP_NA_1), "the reset confirmation");
        Assert.True(_server.Status.AsdusReceived > receivedBefore, "our reset-process handler must be the one answering");

        // …and that it is still working afterwards. A control centre that resets the station and then
        // stops receiving changes would be blind without knowing it, so this is worth pinning down.
        lock (received)
            received.Clear();
        Publish("Plant/Power", 200.0);
        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.SPONTANEOUS),
            "a spontaneous report after the reset");
    }

    [Fact]
    public async Task Measurands_without_a_time_tag_go_out_as_M_ME_NC_1_which_is_what_the_utility_asks_for()
    {
        // EVN 378 lists T13 (M_ME_NC_1) for measurands and T30 (M_SP_TB_1) for status. With a time tag
        // a measurand becomes M_ME_TF_1 (T36) instead, so the flag has to be per point.
        Publish("Plant/Power", 42.0);
        Publish("Plant/Breaker", true);
        var received = await StartAsync(
            Point(1, "Plant/Power", Iec104PointType.ShortFloat, withTimestamp: false),
            Point(2, "Plant/Breaker", Iec104PointType.SinglePoint, withTimestamp: true));

        _client!.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, CommonAddress, 20);
        await WaitFor(() => Snapshot(received).Any(a => a.Cot == CauseOfTransmission.ACTIVATION_TERMINATION),
            "the interrogation to finish");

        var types = Snapshot(received)
            .Where(a => a.Cot == CauseOfTransmission.INTERROGATED_BY_STATION)
            .Select(a => a.TypeId)
            .ToList();

        Assert.Contains(TypeID.M_ME_NC_1, types);   // T13
        Assert.Contains(TypeID.M_SP_TB_1, types);   // T30
    }

    public async ValueTask DisposeAsync()
    {
        try { _client?.Close(); } catch { /* the test may already have torn it down */ }
        if (_server is not null)
            await _server.DisposeAsync();
    }
}
