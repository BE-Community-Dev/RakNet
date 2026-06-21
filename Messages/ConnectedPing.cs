using System;

namespace RakNet.Messages;

/// <summary>
/// Connected ping. Sent over an established RakNet connection to measure
/// round-trip time and keep the connection alive.
/// Layout: [ID:1][PingTime:8 BE] = 9 bytes.
/// </summary>
public class ConnectedPing : IMarshalable
{
    public long PingTime;

    public void Unmarshal(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) throw new EndOfStreamException("ConnectedPing: too short");
        PingTime = (long)Binary.LoadUint64(data);
    }

    public byte[] Marshal()
    {
        byte[] b = new byte[9];
        b[0] = Id.ConnectedPing;
        Binary.PutUint64(b.AsSpan(1), (ulong)PingTime);
        return b;
    }
}

/// <summary>
/// Connected pong. Response to ConnectedPing, echoing the ping timestamp and
/// including the pong (response) timestamp.
/// Layout: [ID:1][PingTime:8 BE][PongTime:8 BE] = 17 bytes.
/// </summary>
public class ConnectedPong : IMarshalable
{
    public long PingTime;
    public long PongTime;

    public void Unmarshal(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) throw new EndOfStreamException("ConnectedPong: too short");
        PingTime = (long)Binary.LoadUint64(data);
        PongTime = (long)Binary.LoadUint64(data.Slice(8));
    }

    public byte[] Marshal()
    {
        byte[] b = new byte[17];
        b[0] = Id.ConnectedPong;
        Binary.PutUint64(b.AsSpan(1), (ulong)PingTime);
        Binary.PutUint64(b.AsSpan(9), (ulong)PongTime);
        return b;
    }
}
