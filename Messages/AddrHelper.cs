using System;
using System.Net;
using System.Net.Sockets;

namespace RakNet.Messages;

/// <summary>
/// Address encoding/decoding helpers for the RakNet protocol. Addresses are
/// encoded in a specific format where IPv4 addresses use bitwise-not for the
/// IP bytes, and IPv6 addresses carry additional scope/flow fields.
/// </summary>
public static class AddrHelper
{
    public const int SizeOfAddr4 = 1 + 4 + 2;       // type + ip4 + port
    public const int SizeOfAddr6 = 1 + 2 + 2 + 4 + 16 + 4; // type + family + port + flow + ip16 + scope

    /// <summary>
    /// Returns the encoded size in bytes of the given address.
    /// </summary>
    public static int SizeOfAddr(IPEndPoint addr)
    {
        if (addr == null) return SizeOfAddr4;
        return addr.AddressFamily == AddressFamily.InterNetworkV6 ? SizeOfAddr6 : SizeOfAddr4;
    }

    /// <summary>
    /// Returns the encoded size in bytes by inspecting the first byte of an
    /// encoded address (4 = IPv4, 6 = IPv6).
    /// </summary>
    public static int AddrSize(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0 || b[0] == 4 || b[0] == 0) return SizeOfAddr4;
        return SizeOfAddr6;
    }

    /// <summary>
    /// Writes an address to the span and returns the number of bytes written.
    /// IPv4: type=4, 4 bytes (bitwise-not of IP), 2 bytes big-endian port.
    /// IPv6: type=6, 2 bytes LE family (23=AF_INET6), 2 bytes BE port, 4 bytes
    /// flow, 16 bytes IP, 4 bytes scope.
    /// </summary>
    public static int PutAddr(Span<byte> b, IPEndPoint addr)
    {
        if (addr == null || addr.Address == null)
        {
            b[0] = 4;
            b[1] = 255; b[2] = 255; b[3] = 255; b[4] = 255;
            Binary.PutUint16(b.Slice(5), 0);
            return SizeOfAddr4;
        }

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] ip = addr.Address.GetAddressBytes(); // 4 bytes
            b[0] = 4;
            b[1] = (byte)~ip[0];
            b[2] = (byte)~ip[1];
            b[3] = (byte)~ip[2];
            b[4] = (byte)~ip[3];
            Binary.PutUint16(b.Slice(5), (ushort)addr.Port);
            return SizeOfAddr4;
        }
        else
        {
            // IPv6
            byte[] ip = addr.Address.GetAddressBytes(); // 16 bytes
            b[0] = 6;
            // Family in little-endian (AF_INET6 = 23 on Windows)
            b[1] = 23;
            b[2] = 0;
            // Port in big-endian
            Binary.PutUint16(b.Slice(3), (ushort)addr.Port);
            // Flow info: 4 bytes at offset 5 (zero)
            b[5] = 0; b[6] = 0; b[7] = 0; b[8] = 0;
            // IP: 16 bytes at offset 9
            ip.CopyTo(b.Slice(9, 16));
            // Scope ID: 4 bytes at offset 25 (zero)
            b[25] = 0; b[26] = 0; b[27] = 0; b[28] = 0;
            return SizeOfAddr6;
        }
    }

    /// <summary>
    /// Reads an address from the span and returns it along with the number of
    /// bytes consumed.
    /// </summary>
    public static (IPEndPoint, int) Addr(ReadOnlySpan<byte> b)
    {
        if (b[0] == 4 || b[0] == 0)
        {
            // IPv4: bytes are bitwise-not of the actual IP
            byte[] ip = new byte[4];
            ip[0] = (byte)((~b[1]) & 0xFF);
            ip[1] = (byte)((~b[2]) & 0xFF);
            ip[2] = (byte)((~b[3]) & 0xFF);
            ip[3] = (byte)((~b[4]) & 0xFF);
            ushort port = Binary.LoadUint16(b.Slice(5));
            return (new IPEndPoint(new IPAddress(ip), port), SizeOfAddr4);
        }
        else
        {
            // IPv6
            ushort port = Binary.LoadUint16(b.Slice(3));
            byte[] ip = b.Slice(9, 16).ToArray();
            return (new IPEndPoint(new IPAddress(ip), port), SizeOfAddr6);
        }
    }
}
