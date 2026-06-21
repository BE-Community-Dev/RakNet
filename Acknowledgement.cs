using System;
using System.Collections.Generic;
using System.IO;

namespace RakNet;

/// <summary>
/// An acknowledgement record type: either a single packet sequence number,
/// or a range of consecutive sequence numbers.
/// </summary>
internal enum AckRecordType : byte
{
    Range = 0,
    Single = 1,
}

/// <summary>
/// An ACK or NACK packet. Contains a list of datagram sequence numbers that
/// are being acknowledged (ACK) or requested for retransmission (NACK).
/// The sequence numbers are run-length encoded as either single entries or
/// contiguous ranges to save bandwidth.
/// </summary>
public class Acknowledgement
{
    public List<uint> Packets = new(); // uint24 values

    /// <summary>
    /// Writes as many acknowledgement records as fit within the MTU to the
    /// buffer. Returns the number of sequence numbers written.
    /// </summary>
    public int Write(MemoryStream buf, ushort mtu)
    {
        int lenOffset = (int)buf.Length;
        Binary.WriteUint16(buf, 0); // Placeholder for record count

        if (Packets.Count == 0) return 0;

        Packets.Sort();
        uint firstInRange = Packets[0];
        uint lastInRange = Packets[0];
        ushort records = 0;
        int n = 0;

        for (int index = 0; index < Packets.Count; index++)
        {
            if (buf.Length >= mtu - 7) break;
            uint pk = Packets[index];
            n++;
            if (index == 0)
            {
                firstInRange = pk;
                lastInRange = pk;
                continue;
            }
            if (pk == lastInRange + 1)
            {
                lastInRange = pk;
                continue;
            }
            WriteRecord(buf, firstInRange, lastInRange, ref records);
            firstInRange = pk;
            lastInRange = pk;
        }
        WriteRecord(buf, firstInRange, lastInRange, ref records);

        // Patch the record count at the placeholder position.
        byte[] bufArray = buf.GetBuffer();
        Binary.PutUint16(bufArray.AsSpan(lenOffset), records);

        return n;
    }

    private static void WriteRecord(MemoryStream buf, uint first, uint last, ref ushort count)
    {
        if (first == last)
        {
            buf.WriteByte((byte)AckRecordType.Single);
            Binary.WriteUint24(buf, first);
        }
        else
        {
            buf.WriteByte((byte)AckRecordType.Range);
            Binary.WriteUint24(buf, first);
            Binary.WriteUint24(buf, last);
        }
        count++;
    }

    /// <summary>
    /// Decodes an acknowledgement packet from the given data (excluding the
    /// leading ID/flag byte).
    /// </summary>
    public void Read(ReadOnlySpan<byte> b)
    {
        const int maxAckPackets = 8192;
        if (b.Length < 2) throw new EndOfStreamException("ack: too short");
        int offset = 2;
        ushort recordCount = Binary.LoadUint16(b);
        for (int i = 0; i < recordCount; i++)
        {
            if (b.Length - offset < 4) throw new EndOfStreamException("ack: record truncated");
            switch (b[offset])
            {
                case (byte)AckRecordType.Range:
                    if (b.Length - offset < 7) throw new EndOfStreamException("ack: range truncated");
                    {
                        uint start = Binary.LoadUint24(b.Slice(offset + 1));
                        uint end = Binary.LoadUint24(b.Slice(offset + 4));
                        if ((uint)Packets.Count + end - start > maxAckPackets)
                            throw new Exception("maximum amount of packets in acknowledgement exceeded");
                        for (uint pk = start; pk <= end; pk++)
                            Packets.Add(pk);
                    }
                    offset += 7;
                    break;
                case (byte)AckRecordType.Single:
                    if (Packets.Count + 1 > maxAckPackets)
                        throw new Exception("maximum amount of packets in acknowledgement exceeded");
                    Packets.Add(Binary.LoadUint24(b.Slice(offset + 1)));
                    offset += 4;
                    break;
            }
        }
    }
}
