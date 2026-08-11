using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace HinoGateway.LoadTests;

/// <summary>
/// Runs the blocking Modbus slave farm in a different process. Keeping both ends of hundreds of
/// sockets in the process being measured used to starve the farm's thread pool and make a healthy
/// gateway look broken; it also polluted the gateway's RSS/thread counters with its own load.
/// </summary>
internal sealed class ExternalSlaveFarm : IAsyncDisposable
{
    private readonly Process _process;

    private ExternalSlaveFarm(Process process, IReadOnlyList<int> ports)
    {
        _process = process;
        Ports = ports;
    }

    public IReadOnlyList<int> Ports { get; }
    public long WorkingSetMb
    {
        get { try { _process.Refresh(); return _process.WorkingSet64 / 1024 / 1024; } catch { return 0; } }
    }
    public int Threads
    {
        get { try { _process.Refresh(); return _process.Threads.Count; } catch { return 0; } }
    }

    public static async Task<ExternalSlaveFarm> StartAsync(IReadOnlyList<SlaveKind> population,
        int latencyMs, CancellationToken ct = default)
    {
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("Cannot locate the load-test executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("slave-farm-child");
        start.ArgumentList.Add("--kinds");
        start.ArgumentList.Add(string.Join(',', population.Select(k => (int)k)));
        start.ArgumentList.Add("--latency");
        start.ArgumentList.Add(latencyMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var process = Process.Start(start)
                      ?? throw new InvalidOperationException("Could not start the external Modbus slave farm.");
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(ct).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (line is null)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException($"Slave farm exited before reporting ports: {error}");
            }
            var ports = JsonSerializer.Deserialize<List<int>>(line)
                        ?? throw new InvalidOperationException("Slave farm returned an invalid port list.");
            if (ports.Count != population.Count)
                throw new InvalidOperationException(
                    $"Slave farm returned {ports.Count} ports for {population.Count} devices.");
            return new ExternalSlaveFarm(process, ports);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_process.HasExited)
                return;
            await _process.StandardInput.WriteLineAsync("stop");
            await _process.StandardInput.FlushAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            _process.Dispose();
        }
    }
}
