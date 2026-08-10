using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace HinoGateway.LoadTests;

/// <summary>A small real Modbus/TCP slave used to inject deterministic link failures.</summary>
public sealed class BlockingModbusSlave : IAsyncDisposable
{
    private readonly SlaveKind _kind;
    private readonly int _latencyMs;
    private readonly int _registerCount;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _clients = [];
    private Task? _acceptLoop;

    public BlockingModbusSlave(SlaveKind kind, int latencyMs, int registerCount)
    {
        _kind = kind;
        _latencyMs = Math.Max(0, latencyMs);
        _registerCount = Math.Max(1, registerCount);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptAsync(_cts.Token));
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                lock (_clients) _clients.Add(Task.Run(() => ServeAsync(client, ct), ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new byte[7];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!await ReadExactlyOrEofAsync(stream, header, ct)) return;
                    var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                    if (length is < 2 or > 260) return;
                    var pdu = new byte[length - 1];
                    if (!await ReadExactlyOrEofAsync(stream, pdu, ct)) return;

                    if (_kind == SlaveKind.BlackHole)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                        return;
                    }
                    if (_latencyMs > 0) await Task.Delay(_latencyMs, ct);

                    var response = BuildResponse(pdu);
                    var frame = new byte[7 + response.Length];
                    header.AsSpan(0, 4).CopyTo(frame);
                    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), checked((ushort)(response.Length + 1)));
                    frame[6] = header[6];
                    response.CopyTo(frame, 7);
                    await stream.WriteAsync(frame, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    private byte[] BuildResponse(byte[] pdu)
    {
        if (pdu.Length < 5) return Exception(pdu.FirstOrDefault(), 3);
        var function = pdu[0];
        if (_kind == SlaveKind.Refused) return Exception(function, 4);
        if (function != 3) return Exception(function, 1);

        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
        if (count == 0 || count > 125 || start + count > _registerCount)
            return Exception(function, 2);

        var response = new byte[2 + count * 2];
        response[0] = function;
        response[1] = checked((byte)(count * 2));
        for (var i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + i * 2, 2), checked((ushort)(start + i)));
        return response;
    }

    private static byte[] Exception(byte function, byte code) => [(byte)(function | 0x80), code];

    private static async Task<bool> ReadExactlyOrEofAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
            try { await _acceptLoop; } catch { }
        Task[] clients;
        lock (_clients) clients = _clients.ToArray();
        try { await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
