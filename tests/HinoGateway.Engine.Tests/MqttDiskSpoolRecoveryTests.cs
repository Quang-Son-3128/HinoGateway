using HinoGateway.Northbound.Mqtt;

namespace HinoGateway.Engine.Tests;

public sealed class MqttDiskSpoolRecoveryTests
{
    [Fact]
    public async Task Spool_survives_restart_and_preserves_fifo_order()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-mqtt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new MqttDiskSpool(directory, 2 * 1024 * 1024);
            await first.EnqueueAsync([1, 2, 3], CancellationToken.None);
            await first.EnqueueAsync([4, 5], CancellationToken.None);

            var reopened = new MqttDiskSpool(directory, 2 * 1024 * 1024);
            var one = await reopened.PeekAsync(CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3 }, one!.Payload);
            await reopened.CompleteAsync(one, CancellationToken.None);
            var two = await reopened.PeekAsync(CancellationToken.None);
            Assert.Equal(new byte[] { 4, 5 }, two!.Payload);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Spool_drops_oldest_batches_at_size_limit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-mqtt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new MqttDiskSpool(directory, 1024 * 1024);
            await spool.EnqueueAsync(new byte[700_000], CancellationToken.None);
            await spool.EnqueueAsync(Enumerable.Repeat((byte)7, 700_000).ToArray(), CancellationToken.None);
            Assert.Equal(1, spool.Stats().Count);
            Assert.Equal(1, spool.Dropped);
            Assert.Equal(7, (await spool.PeekAsync(CancellationToken.None))!.Payload[0]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Small_batches_share_a_bounded_segment_instead_of_creating_one_file_each()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-mqtt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new MqttDiskSpool(directory, 2 * 1024 * 1024);
            for (var i = 0; i < 100; i++)
                await spool.EnqueueAsync([(byte)i], CancellationToken.None);

            Assert.Equal(100, spool.Stats().Count);
            Assert.Single(Directory.EnumerateFiles(directory, "*.seg"));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.msg"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Recovery_discards_a_torn_tail_but_keeps_complete_records()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-mqtt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new MqttDiskSpool(directory, 2 * 1024 * 1024);
            await spool.EnqueueAsync([1, 2, 3], CancellationToken.None);
            var segment = Assert.Single(Directory.EnumerateFiles(directory, "*.seg"));
            await using (var tail = new FileStream(segment, FileMode.Append, FileAccess.Write, FileShare.Read))
                await tail.WriteAsync(new byte[] { 100, 0, 0 }); // incomplete next header

            var recovered = new MqttDiskSpool(directory, 2 * 1024 * 1024);
            Assert.Equal(1, recovered.Stats().Count);
            Assert.Equal(new byte[] { 1, 2, 3 }, (await recovered.PeekAsync(CancellationToken.None))!.Payload);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
