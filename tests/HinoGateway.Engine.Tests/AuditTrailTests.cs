using HinoGateway.Server;
using HinoGateway.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

public class AuditTrailTests
{
    [Fact]
    public async Task Concurrent_entries_are_durably_written_by_the_batch_worker()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:BatchWindowMs"] = "5",
                ["Audit:BatchSize"] = "100",
            }).Build();
            var health = new ComponentHealthRegistry(configuration);
            var audit = new AuditTrail(new GatewayDataPaths(directory), configuration, health,
                NullLogger<AuditTrail>.Instance);
            await audit.StartAsync(CancellationToken.None);

            await Task.WhenAll(Enumerable.Range(0, 50).Select(i => audit.WriteAsync(new GatewayAuditEvent(
                DateTimeOffset.UtcNow, "test", "tester", "unit", null, i.ToString(), "Successful"))));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await audit.StopAsync(stop.Token);

            var lines = await File.ReadAllLinesAsync(Directory.EnumerateFiles(Path.Combine(directory, "audit"), "*.jsonl").Single());
            Assert.Equal(50, lines.Length);
            Assert.All(lines, line => Assert.Contains("\"action\":\"test\"", line));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
