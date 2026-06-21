using System;
using System.IO;

namespace RakNet;

/// <summary>
/// Binary helper utilities for the RakNet protocol. Provides uint24 (3-byte)
/// read/write helpers and big-endian writers matching the Go implementation.
/// </summary>
public static class Binary
{
    public const uint Uint24Mask = 0xFFFFFF;

    /// <summary>
    /// Reads 3 bytes from the buffer at the current position and combines them
    /// into a uint24 (little-endian within the 3 bytes, as per RakNet).
    /// </summary>
    public static uint LoadUint24(ReadOnlySpan<byte> b)
    {
        return (uint)(b[0] | (b[1] << 8) | (b[2] << 16));
    }

    /// <summary>
    /// Writes a uint24 value as 3 bytes to the stream.
    /// </summary>
    public static void WriteUint24(MemoryStream buf, uint v)
    {
        buf.WriteByte((byte)(v & 0xFF));
        buf.WriteByte((byte)((v >> 8) & 0xFF));
        buf.WriteByte((byte)((v >> 16) & 0xFF));
    }

    /// <summary>
    /// Writes a uint16 value in big-endian to the stream.
    /// </summary>
    public static void WriteUint16(MemoryStream buf, ushort v)
    {
        buf.WriteByte((byte)((v >> 8) & 0xFF));
        buf.WriteByte((byte)(v & 0xFF));
    }

    /// <summary>
    /// Writes a uint32 value in big-endian to the stream.
    /// </summary>
    public static void WriteUint32(MemoryStream buf, uint v)
    {
        buf.WriteByte((byte)((v >> 24) & 0xFF));
        buf.WriteByte((byte)((v >> 16) & 0xFF));
        buf.WriteByte((byte)((v >> 8) & 0xFF));
        buf.WriteByte((byte)(v & 0xFF));
    }

    /// <summary>
    /// Writes a uint64 value in big-endian to the stream.
    /// </summary>
    public static void WriteUint64(MemoryStream buf, ulong v)
    {
        buf.WriteByte((byte)((v >> 56) & 0xFF));
        buf.WriteByte((byte)((v >> 48) & 0xFF));
        buf.WriteByte((byte)((v >> 40) & 0xFF));
        buf.WriteByte((byte)((v >> 32) & 0xFF));
        buf.WriteByte((byte)((v >> 24) & 0xFF));
        buf.WriteByte((byte)((v >> 16) & 0xFF));
        buf.WriteByte((byte)((v >> 8) & 0xFF));
        buf.WriteByte((byte)(v & 0xFF));
    }

    /// <summary>
    /// Reads a uint16 in big-endian from the span.
    /// </summary>
    public static ushort LoadUint16(ReadOnlySpan<byte> b)
    {
        return (ushort)((b[0] << 8) | b[1]);
    }

    /// <summary>
    /// Reads a uint32 in big-endian from the span.
    /// </summary>
    public static uint LoadUint32(ReadOnlySpan<byte> b)
    {
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    /// <summary>
    /// Reads a uint64 in big-endian from the span.
    /// </summary>
    public static ulong LoadUint64(ReadOnlySpan<byte> b)
    {
        return ((ulong)b[0] << 56) | ((ulong)b[1] << 48) | ((ulong)b[2] << 40) |
               ((ulong)b[3] << 32) | ((ulong)b[4] << 24) | ((ulong)b[5] << 16) |
               ((ulong)b[6] << 8) | (ulong)b[7];
    }

    /// <summary>
    /// Writes a uint16 in big-endian into a byte span at the given offset.
    /// </summary>
    public static void PutUint16(Span<byte> b, ushort v)
    {
        b[0] = (byte)((v >> 8) & 0xFF);
        b[1] = (byte)(v & 0xFF);
    }

    /// <summary>
    /// Writes a uint32 in big-endian into a byte span at the given offset.
    /// </summary>
    public static void PutUint32(Span<byte> b, uint v)
    {
        b[0] = (byte)((v >> 24) & 0xFF);
        b[1] = (byte)((v >> 16) & 0xFF);
        b[2] = (byte)((v >> 8) & 0xFF);
        b[3] = (byte)(v & 0xFF);
    }

    /// <summary>
    /// Writes a uint64 in big-endian into a byte span at the given offset.
    /// </summary>
    public static void PutUint64(Span<byte> b, ulong v)
    {
        b[0] = (byte)((v >> 56) & 0xFF);
        b[1] = (byte)((v >> 48) & 0xFF);
        b[2] = (byte)((v >> 40) & 0xFF);
        b[3] = (byte)((v >> 32) & 0xFF);
        b[4] = (byte)((v >> 24) & 0xFF);
        b[5] = (byte)((v >> 16) & 0xFF);
        b[6] = (byte)((v >> 8) & 0xFF);
        b[7] = (byte)(v & 0xFF);
    }

    /// <summary>
    /// Writes a uint32 in little-endian into a byte span (used for cookie salt).
    /// </summary>
    public static void PutUint32LE(Span<byte> b, uint v)
    {
        b[0] = (byte)(v & 0xFF);
        b[1] = (byte)((v >> 8) & 0xFF);
        b[2] = (byte)((v >> 16) & 0xFF);
        b[3] = (byte)((v >> 24) & 0xFF);
    }

    /// <summary>
    /// Writes a uint64 in little-endian into a byte span (used for cookie salt).
    /// </summary>
    public static void PutUint64LE(Span<byte> b, ulong v)
    {
        b[0] = (byte)(v & 0xFF);
        b[1] = (byte)((v >> 8) & 0xFF);
        b[2] = (byte)((v >> 16) & 0xFF);
        b[3] = (byte)((v >> 24) & 0xFF);
        b[4] = (byte)((v >> 32) & 0xFF);
        b[5] = (byte)((v >> 40) & 0xFF);
        b[6] = (byte)((v >> 48) & 0xFF);
        b[7] = (byte)((v >> 56) & 0xFF);
    }
}
