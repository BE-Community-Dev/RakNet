using System;
using System.Net;

namespace RakNet.Messages;

/// <summary>
/// Open connection request 2. After MTU discovery, the client sends this to
/// request a connection, including the server address, agreed MTU, and client
/// GUID. If the server has security enabled, a cookie is included.
/// </summary>
public class OpenConnectionRequest2
{
    public IPEndPoint? ServerAddress;
    public ushort MTU;
    public long ClientGuid;
    public bool ServerHasSecurity;
    public uint Cookie;

    public byte[] Marshal()
    {
        int cookieOffset = ServerHasSecurity ? 5 : 0;
        int addrSize = AddrHelper.SizeOfAddr(ServerAddress);
        byte[] b = new byte[27 + addrSize + cookieOffset];
        b[0] = Id.OpenConnectionRequest2;
        Buffer.BlockCopy(Magic.Sequence, 0, b, 1, 16);
        if (ServerHasSecurity)
        {
            Binary.PutUint32(b.AsSpan(17), Cookie);
        }
        int written = AddrHelper.PutAddr(b.AsSpan(17 + cookieOffset), ServerAddress);
        int afterAddr = 17 + cookieOffset + written;
        Binary.PutUint16(b.AsSpan(afterAddr), MTU);
        Binary.PutUint64(b.AsSpan(afterAddr + 2), (ulong)ClientGuid);
        return b;
    }

    public void Unmarshal(ReadOnlySpan<byte> data)
    {
        int cookieOffset = ServerHasSecurity ? 5 : 0;
        if (data.Length < 16 + cookieOffset ||
            data.Length < 26 + cookieOffset + AddrHelper.AddrSize(data.Slice(16 + cookieOffset)))
            throw new EndOfStreamException("OpenConnectionRequest2: too short");
        if (ServerHasSecurity)
        {
            Cookie = Binary.LoadUint32(data.Slice(16));
        }
        int offset = cookieOffset;
        (ServerAddress, int n) = AddrHelper.Addr(data.Slice(16 + offset));
        offset += n;
        MTU = Binary.LoadUint16(data.Slice(16 + offset));
        ClientGuid = (long)Binary.LoadUint64(data.Slice(18 + offset));
    }
}

/// <summary>
/// Open connection reply 2. The server sends this after accepting
/// OpenConnectionRequest2, confirming the connection and the final MTU.
/// </summary>
public class OpenConnectionReply2
{
    public long ServerGuid;
    public IPEndPoint? ClientAddress;
    public ushort MTU;
    public bool DoSecurity;

    public void Unmarshal(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 || data.Length < 27 + AddrHelper.AddrSize(data.Slice(24)))
            throw new EndOfStreamException("OpenConnectionReply2: too short");
        // Magic: 16 bytes (skipped)
        ServerGuid = (long)Binary.LoadUint64(data.Slice(16));
        (ClientAddress, int offset) = AddrHelper.Addr(data.Slice(24));
        MTU = Binary.LoadUint16(data.Slice(24 + offset));
        DoSecurity = data[26 + offset] != 0;
    }

    public byte[] Marshal()
    {
        int addrSize = AddrHelper.SizeOfAddr(ClientAddress);
        byte[] b = new byte[28 + addrSize];
        b[0] = Id.OpenConnectionReply2;
        Buffer.BlockCopy(Magic.Sequence, 0, b, 1, 16);
        Binary.PutUint64(b.AsSpan(17), (ulong)ServerGuid);
        int written = AddrHelper.PutAddr(b.AsSpan(25), ClientAddress);
        Binary.PutUint16(b.AsSpan(25 + written), MTU);
        if (DoSecurity) b[27 + written] = 1;
        return b;
    }
}
