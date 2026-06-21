using System;
using System.IO;

namespace RakNet.Messages;

/// <summary>
/// Unconnected ping packet. Sent by a client to discover servers. The server
/// responds with an UnconnectedPong containing server info data.
/// Layout: [ID:1][PingTime:8 BE][Magic:16][ClientGUID:8 BE] = 33 bytes.
/// </summary>
public class UnconnectedPing
{
    public long PingTime;
    public long ClientGuid;

    public byte[] Marshal()
    {
        byte[] b = new byte[33];
        b[0] = Id.UnconnectedPing;
        Binary.PutUint64(b.AsSpan(1), (ulong)PingTime);
        Buffer.BlockCopy(Magic.Sequence, 0, b, 9, 16);
        Binary.PutUint64(b.AsSpan(25), (ulong)ClientGuid);
        return b;
    }

    public void Unmarshal(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32) throw new EndOfStreamException("UnconnectedPing: too short");
        PingTime = (long)Binary.LoadUint64(data);
        // Magic: 16 bytes (skipped)
        ClientGuid = (long)Binary.LoadUint64(data.Slice(24));
    }
}

/// <summary>
/// Unconnected pong packet. Sent by a server in response to an UnconnectedPing.
/// Layout: [ID:1][PingTime:8 BE][ServerGUID:8 BE][Magic:16][DataLen:2 BE][Data:N].
/// </summary>
public class UnconnectedPong
{
    public long PingTime;
    public long ServerGuid;
    public byte[]? Data;

    public void Unmarshal(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32) throw new EndOfStreamException("UnconnectedPong: too short");
        PingTime = (long)Binary.LoadUint64(data);
        ServerGuid = (long)Binary.LoadUint64(data.Slice(8));
        if (data.Length < 34)
        {
            Data = null;
            return;
        }
        int n = Binary.LoadUint16(data.Slice(32));
        if (data.Length < 34 + n) throw new EndOfStreamException("UnconnectedPong: data truncated");
        if (n == 0) { Data = null; return; }
        Data = data.Slice(34, n).ToArray();
    }

    public byte[] Marshal()
    {
        int dataLen = Data?.Length ?? 0;
        byte[] b = new byte[35 + dataLen];
        b[0] = Id.UnconnectedPong;
        Binary.PutUint64(b.AsSpan(1), (ulong)PingTime);
        Binary.PutUint64(b.AsSpan(9), (ulong)ServerGuid);
        Buffer.BlockCopy(Magic.Sequence, 0, b, 17, 16);
        Binary.PutUint16(b.AsSpan(33), (ushort)dataLen);
        if (Data != null) Buffer.BlockCopy(Data, 0, b, 35, dataLen);
        return b;
    }
}
