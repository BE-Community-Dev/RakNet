using System;
using System.IO;

namespace RakNet;

/// <summary>
/// Protocol-level constants for RakNet frames and MTU limits.
/// </summary>
public static class RakNetConstants
{
    public const byte BitFlagDatagram = 0x80;
    public const byte BitFlagAck = 0x40;
    public const byte BitFlagNack = 0x20;
    public const byte BitFlagNeedsBAndAS = 0x04;

    public const byte SplitFlag = 0x10;

    public const byte ProtocolVersion = 11;
    public const ushort MinMTUSize = 400;
    public const ushort MaxMTUSize = 1492;
    public const int MaxWindowSize = 2048;

    public const int PacketAdditionalSize = 1 + 3 + 1 + 2 + 3 + 3 + 1; // 14
    public const int SplitAdditionalSize = 4 + 2 + 4; // 10
}

/// <summary>
/// Reliability levels for RakNet packets. Higher reliability means more
/// guarantees about delivery and ordering, at the cost of more overhead.
/// </summary>
public enum Reliability : byte
{
    Unreliable = 0,
    UnreliableSequenced = 1,
    Reliable = 2,
    ReliableOrdered = 3,
    ReliableSequenced = 4,
}

public static class ReliabilityExtensions
{
    public static bool Reliable(this Reliability r) =>
        r == Reliability.Reliable ||
        r == Reliability.ReliableOrdered ||
        r == Reliability.ReliableSequenced;

    public static bool Sequenced(this Reliability r) =>
        r == Reliability.UnreliableSequenced ||
        r == Reliability.ReliableSequenced;

    public static bool SequencedOrOrdered(this Reliability r) =>
        r.Sequenced() || r == Reliability.ReliableOrdered;
}

/// <summary>
/// An encapsulated packet sent within a datagram after the connection is
/// established. Each packet carries reliability metadata, optional message/
/// sequence/order indices, and optional split fragmentation info.
/// </summary>
public class Packet
{
    public Reliability Reliability;
    public uint MessageIndex;  // uint24
    public uint SequenceIndex; // uint24
    public uint OrderIndex;    // uint24
    public byte[] Content = Array.Empty<byte>();
    public bool Split;
    public uint SplitCount;
    public uint SplitIndex;
    public ushort SplitId;

    /// <summary>
    /// Writes the packet encapsulation (header + indices + content) to the
    /// stream. Does not write the datagram frame.
    /// </summary>
    public void Write(MemoryStream buf)
    {
        byte header = (byte)((int)Reliability << 5);
        if (Split) header |= RakNetConstants.SplitFlag;

        buf.WriteByte(header);
        Binary.WriteUint16(buf, (ushort)(Content.Length << 3));

        if (Reliability.Reliable())
            Binary.WriteUint24(buf, MessageIndex);
        if (Reliability.Sequenced())
            Binary.WriteUint24(buf, SequenceIndex);
        if (Reliability.SequencedOrOrdered())
        {
            Binary.WriteUint24(buf, OrderIndex);
            buf.WriteByte(0); // Order channel, unused.
        }
        if (Split)
        {
            Binary.WriteUint32(buf, SplitCount);
            Binary.WriteUint16(buf, SplitId);
            Binary.WriteUint32(buf, SplitIndex);
        }
        buf.Write(Content, 0, Content.Length);
    }

    /// <summary>
    /// Reads a single packet encapsulation from the buffer. Returns the number
    /// of bytes consumed.
    /// </summary>
    public int Read(ReadOnlySpan<byte> b)
    {
        if (b.Length < 3) throw new EndOfStreamException("packet: too short for header");
        byte header = b[0];
        Split = (header & RakNetConstants.SplitFlag) != 0;
        Reliability = (Reliability)((header & 0xE0) >> 5);

        int n = Binary.LoadUint16(b.Slice(1)) >> 3;
        if (n == 0) throw new Exception("invalid packet length: cannot be 0");
        int offset = 3;

        if (Reliability.Reliable())
        {
            if (b.Length - offset < 3) throw new EndOfStreamException("packet: messageIndex truncated");
            MessageIndex = Binary.LoadUint24(b.Slice(offset));
            offset += 3;
        }
        if (Reliability.Sequenced())
        {
            if (b.Length - offset < 3) throw new EndOfStreamException("packet: sequenceIndex truncated");
            SequenceIndex = Binary.LoadUint24(b.Slice(offset));
            offset += 3;
        }
        if (Reliability.SequencedOrOrdered())
        {
            if (b.Length - offset < 4) throw new EndOfStreamException("packet: orderIndex truncated");
            OrderIndex = Binary.LoadUint24(b.Slice(offset));
            offset += 4; // orderIndex (3) + order channel (1)
        }
        if (Split)
        {
            if (b.Length - offset < 10) throw new EndOfStreamException("packet: split fields truncated");
            SplitCount = Binary.LoadUint32(b.Slice(offset));
            SplitId = Binary.LoadUint16(b.Slice(offset + 4));
            SplitIndex = Binary.LoadUint32(b.Slice(offset + 6));
            offset += 10;
        }

        Content = new byte[n];
        if (b.Slice(offset).Length < n) throw new EndOfStreamException("packet: content truncated");
        b.Slice(offset, n).CopyTo(Content);
        return offset + n;
    }
}

public static class PacketSplitter
{
    /// <summary>
    /// Splits a content buffer into fragments that each fit within the MTU.
    /// If splitting occurs, each fragment is further reduced by the split
    /// overhead (4+2+4 bytes for splitCount, splitID, splitIndex).
    /// </summary>
    public static byte[][] Split(byte[] b, ushort mtu)
    {
        int n = b.Length;
        int maxSize = mtu - RakNetConstants.PacketAdditionalSize;
        if (n > maxSize)
            maxSize -= RakNetConstants.SplitAdditionalSize;

        int fragmentCount = n / maxSize + Math.Min(n % maxSize, 1);
        byte[][] fragments = new byte[fragmentCount][];
        int srcOffset = 0;
        for (int i = 0; i < fragmentCount - 1; i++)
        {
            fragments[i] = new byte[maxSize];
            Array.Copy(b, srcOffset, fragments[i], 0, maxSize);
            srcOffset += maxSize;
        }
        int lastLen = n - srcOffset;
        fragments[fragmentCount - 1] = new byte[lastLen];
        Array.Copy(b, srcOffset, fragments[fragmentCount - 1], 0, lastLen);
        return fragments;
    }
}
