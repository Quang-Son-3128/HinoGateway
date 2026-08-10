using System.Net.Sockets;

namespace HinoGateway.Core;

/// <summary>Conservatively identifies errors that prove the connection itself is gone.</summary>
public static class TransportFaults
{
    public static bool IsConnectionGone(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SocketException socket when IsFatal(socket.SocketErrorCode):
                case EndOfStreamException:
                case ObjectDisposedException:
                    return true;
            }
        }
        return false;
    }

    private static bool IsFatal(SocketError error) => error switch
    {
        SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkReset
            or SocketError.Shutdown or SocketError.NotConnected or SocketError.ConnectionRefused
            or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown
            or SocketError.HostDown => true,
        _ => false,
    };
}
