using System.Net.Sockets;

namespace RakNet;

/// <summary>
/// Best-effort socket buffer sizing. Windows defaults a UDP socket to roughly 64 KB of receive
/// buffer, which a switch-time chunk burst (dozens of full-MTU datagrams per tick) can overflow
/// before the read thread drains it - the kernel then drops datagrams and RakNet pays with a
/// retransmission storm exactly when the player is staring at a loading screen.
/// </summary>
internal static class SocketTuning
{
    private const int ReceiveBufferSize = 4 * 1024 * 1024;
    private const int SendBufferSize = 1024 * 1024;

    public static void Enlarge(Socket udp)
    {
        try
        {
            udp.ReceiveBufferSize = ReceiveBufferSize;
            udp.SendBufferSize = SendBufferSize;
        }
        catch (SocketException)
        {
            // Best effort only: the platform may cap the values; defaults still work.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Windows-only: tells the kernel not to surface a prior send-to-closed-port as ECONNRESET on
    /// the next receive. Without this, the listener's read loop would see a phantom connection
    /// reset roughly whenever one of our replies raced a client's closing socket.
    /// </summary>
    public static void DisableUdpConnReset(Socket udp)
    {
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            udp.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        }
        catch
        {
            // Non-Windows or unsupported: the receive-side ECONNRESET handler covers it.
        }
    }
}
