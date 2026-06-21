using System;

namespace RakNet.Messages;

/// <summary>
/// Connection request. Sent by the client after the open connection handshake
/// to request a full RakNet connection.
/// Layout: [ID:1][ClientGUID:8 BE][RequestTime:8 BE][Secure:1] = 18 bytes.
/// </summary>
public class ConnectionRequest : IMarshalable
{
    public long ClientGuid;
    public long RequestTime;
    public bool Secure;

    public void Unmarshal(ReadOnlySpan<byte> data)
    {
        if (data.Length < 17) throw new EndOfStreamException("ConnectionRequest: too short");
        ClientGuid = (long)Binary.LoadUint64(data);
        RequestTime = (long)Binary.LoadUint64(data.Slice(8));
        Secure = data[16] != 0;
    }

    public byte[] Marshal()
    {
        byte[] b = new byte[18];
        b[0] = Id.ConnectionRequest;
        Binary.PutUint64(b.AsSpan(1), (ulong)ClientGuid);
        Binary.PutUint64(b.AsSpan(9), (ulong)RequestTime);
        if (Secure) b[17] = 1;
        return b;
    }
}
