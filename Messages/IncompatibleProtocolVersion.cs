using System;

namespace RakNet.Messages;

/// <summary>
/// Incompatible protocol version. Sent by the server when the client's protocol
/// version does not match the server's.
/// Layout: [ID:1][ServerProtocol:1][Magic:16][ServerGUID:8 BE] = 26 bytes.
/// </summary>
public class IncompatibleProtocolVersion
{
    public byte ServerProtocol;
    public long ServerGuid;

    public void Unmarshal(ReadOnlySpan<byte> data)
    {
        if (data.Length < 25) throw new EndOfStreamException("IncompatibleProtocolVersion: too short");
        ServerProtocol = data[0];
        // Magic: 16 bytes (skipped)
        ServerGuid = (long)Binary.LoadUint64(data.Slice(17));
    }

    public byte[] Marshal()
    {
        byte[] b = new byte[26];
        b[0] = Id.IncompatibleProtocolVersion;
        b[1] = ServerProtocol;
        Buffer.BlockCopy(Magic.Sequence, 0, b, 2, 16);
        Binary.PutUint64(b.AsSpan(18), (ulong)ServerGuid);
        return b;
    }
}
