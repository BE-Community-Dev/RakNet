using System;

namespace RakNet.Messages;

/// <summary>
/// Open connection request 1. The client sends this with a padding that fills
/// the packet to the desired MTU size, so the server can echo back the MTU.
/// The MTU is inferred from the total packet length on the receiving side.
/// Layout: [ID:1][Magic:16][ClientProtocol:1][padding to MTU-28].
/// </summary>
public class OpenConnectionRequest1
{
    public byte ClientProtocol;
    public ushort MTU;

    public byte[] Marshal()
    {
        // Total size = MTU - 20 (IP header) - 8 (UDP header)
        int size = MTU - 20 - 8;
        byte[] b = new byte[size];
        b[0] = Id.OpenConnectionRequest1;
        Buffer.BlockCopy(Magic.Sequence, 0, b, 1, 16);
        b[17] = ClientProtocol;
        return b;
    }

    public void Unmarshal(ReadOnlySpan<byte> data)
    {
        if (data.Length < 17) throw new EndOfStreamException("OpenConnectionRequest1: too short");
        ClientProtocol = data[16];
        // MTU = data length + 20 (IP) + 8 (UDP) + 1 (packet ID, already stripped by caller in Go)
        // In Go, data passed to Unmarshal excludes the ID byte, so len(data) = total - 1.
        // MTU = len(data) + 20 + 8 + 1
        MTU = (ushort)(data.Length + 20 + 8 + 1);
    }
}

/// <summary>
/// Open connection reply 1. The server responds with this after receiving
/// OpenConnectionRequest1, confirming the MTU and optionally providing a cookie
/// for security.
/// Layout: [ID:1][Magic:16][ServerGUID:8 BE][Security:1][Cookie:4 BE?][MTU:2 BE].
/// </summary>
public class OpenConnectionReply1
{
    public long ServerGuid;
    public bool ServerHasSecurity;
    public uint Cookie;
    public ushort MTU;

    public void Unmarshal(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        if (data.Length < 27 || data.Length < 27 + data[24] * 4)
            throw new EndOfStreamException("OpenConnectionReply1: too short");
        // Magic: 16 bytes (skipped)
        ServerGuid = (long)Binary.LoadUint64(data.Slice(16));
        ServerHasSecurity = data[24] != 0;
        if (ServerHasSecurity)
        {
            offset = 4;
            Cookie = Binary.LoadUint32(data.Slice(25));
        }
        MTU = Binary.LoadUint16(data.Slice(25 + offset));
    }

    public byte[] Marshal()
    {
        int offset = ServerHasSecurity ? 4 : 0;
        byte[] b = new byte[28 + offset];
        b[0] = Id.OpenConnectionReply1;
        Buffer.BlockCopy(Magic.Sequence, 0, b, 1, 16);
        Binary.PutUint64(b.AsSpan(17), (ulong)ServerGuid);
        if (ServerHasSecurity)
        {
            b[25] = 1;
            Binary.PutUint32(b.AsSpan(26), Cookie);
        }
        Binary.PutUint16(b.AsSpan(26 + offset), MTU);
        return b;
    }
}
