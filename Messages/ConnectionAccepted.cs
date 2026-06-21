using System;
using System.Net;

namespace RakNet.Messages;

/// <summary>
/// A fixed-size array of 20 system addresses used in connection handshake
/// packets. These are typically loopback/blank addresses in vanilla RakNet.
/// </summary>
public class SystemAddresses
{
    public const int Count = 20;
    public IPEndPoint?[] Addresses = new IPEndPoint?[Count];

    public int SizeOf()
    {
        int size = 0;
        foreach (var addr in Addresses)
            size += AddrHelper.SizeOfAddr(addr);
        return size;
    }
}

/// <summary>
/// Connection request accepted. Sent by the server in response to
/// ConnectionRequest, echoing the ping time and providing the pong timestamp.
/// Includes the client address, system index, 20 system addresses, and
/// ping/pong timestamps.
/// </summary>
public class ConnectionRequestAccepted : IMarshalable
{
    public IPEndPoint? ClientAddress;
    public ushort SystemIndex;
    public SystemAddresses SystemAddresses = new();
    public long PingTime;
    public long PongTime;

    public void Unmarshal(ReadOnlySpan<byte> data)
    {
        if (data.Length < AddrHelper.AddrSize(data))
            throw new EndOfStreamException("ConnectionRequestAccepted: too short");
        int offset;
        (ClientAddress, offset) = AddrHelper.Addr(data);
        SystemIndex = Binary.LoadUint16(data.Slice(offset));
        offset += 2;
        for (int i = 0; i < SystemAddresses.Count; i++)
        {
            if (data.Slice(offset).Length == 16)
                break;
            if (data.Slice(offset).Length < AddrHelper.AddrSize(data.Slice(offset)))
                throw new EndOfStreamException("ConnectionRequestAccepted: system address truncated");
            var (address, n) = AddrHelper.Addr(data.Slice(offset));
            SystemAddresses.Addresses[i] = address;
            offset += n;
        }
        if (data.Slice(offset).Length < 16)
            throw new EndOfStreamException("ConnectionRequestAccepted: timestamps truncated");
        PingTime = (long)Binary.LoadUint64(data.Slice(offset));
        PongTime = (long)Binary.LoadUint64(data.Slice(offset + 8));
    }

    public byte[] Marshal()
    {
        int nAddr = AddrHelper.SizeOfAddr(ClientAddress);
        int nSys = SystemAddresses.SizeOf();
        byte[] b = new byte[1 + nAddr + 2 + nSys + 16];
        b[0] = Id.ConnectionRequestAccepted;
        int offset = 1 + AddrHelper.PutAddr(b.AsSpan(1), ClientAddress);
        Binary.PutUint16(b.AsSpan(offset), SystemIndex);
        for (int i = 0; i < SystemAddresses.Count; i++)
        {
            offset += AddrHelper.PutAddr(b.AsSpan(offset + 2), SystemAddresses.Addresses[i]);
        }
        Binary.PutUint64(b.AsSpan(offset + 2), (ulong)PingTime);
        Binary.PutUint64(b.AsSpan(offset + 10), (ulong)PongTime);
        return b;
    }
}

/// <summary>
/// New incoming connection. Sent by the client after receiving
/// ConnectionRequestAccepted, finalizing the connection from the client side.
/// Includes the server address, 20 system addresses, and ping/pong timestamps.
/// </summary>
public class NewIncomingConnection : IMarshalable
{
    public IPEndPoint? ServerAddress;
    public SystemAddresses SystemAddresses = new();
    public long PingTime;
    public long PongTime;

    public void Unmarshal(ReadOnlySpan<byte> data)
    {
        if (data.Length < AddrHelper.AddrSize(data))
            throw new EndOfStreamException("NewIncomingConnection: too short");
        int offset;
        (ServerAddress, offset) = AddrHelper.Addr(data);
        for (int i = 0; i < SystemAddresses.Count; i++)
        {
            if (data.Slice(offset).Length == 16)
                break;
            if (data.Slice(offset).Length < AddrHelper.AddrSize(data.Slice(offset)))
                throw new EndOfStreamException("NewIncomingConnection: system address truncated");
            var (address, n) = AddrHelper.Addr(data.Slice(offset));
            SystemAddresses.Addresses[i] = address;
            offset += n;
        }
        if (data.Slice(offset).Length < 16)
            throw new EndOfStreamException("NewIncomingConnection: timestamps truncated");
        PingTime = (long)Binary.LoadUint64(data.Slice(offset));
        PongTime = (long)Binary.LoadUint64(data.Slice(offset + 8));
    }

    public byte[] Marshal()
    {
        int nAddr = AddrHelper.SizeOfAddr(ServerAddress);
        int nSys = SystemAddresses.SizeOf();
        byte[] b = new byte[1 + nAddr + nSys + 16];
        b[0] = Id.NewIncomingConnection;
        int offset = 1 + AddrHelper.PutAddr(b.AsSpan(1), ServerAddress);
        for (int i = 0; i < SystemAddresses.Count; i++)
        {
            offset += AddrHelper.PutAddr(b.AsSpan(offset), SystemAddresses.Addresses[i]);
        }
        Binary.PutUint64(b.AsSpan(offset), (ulong)PingTime);
        Binary.PutUint64(b.AsSpan(offset + 8), (ulong)PongTime);
        return b;
    }
}
