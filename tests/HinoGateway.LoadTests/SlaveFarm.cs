namespace HinoGateway.LoadTests;

public enum SlaveKind { Healthy, Slow, BlackHole, Refused, BadAddress }

/// <summary>Owns one loopback Modbus/TCP endpoint per simulated field device.</summary>
public sealed class SlaveFarm : IAsyncDisposable
{
    public const int BadAddressRegisterCount = 32;
    private readonly List<BlockingModbusSlave> _slaves = [];

    public IReadOnlyList<int> Ports => _slaves.Select(s => s.Port).ToList();

    public void Start(IReadOnlyList<SlaveKind> population, int linkLatencyMs)
    {
        foreach (var kind in population)
        {
            var latency = kind == SlaveKind.Slow ? Math.Max(1, linkLatencyMs == 0 ? 200 : linkLatencyMs) : 0;
            var registers = kind == SlaveKind.BadAddress ? BadAddressRegisterCount : ushort.MaxValue;
            var slave = new BlockingModbusSlave(kind, latency, registers);
            slave.Start();
            _slaves.Add(slave);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var slave in _slaves) await slave.DisposeAsync();
        _slaves.Clear();
    }
}
