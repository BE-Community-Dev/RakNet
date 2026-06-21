using System;

namespace RakNet;

/// <summary>
/// CRC32 (IEEE polynomial 0xEDB88320) implementation used for cookie
/// verification in the listener. Not cryptographically secure, but fast
/// and sufficient for RakNet's anti-spoofing cookie mechanism.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        uint[] t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
            {
                c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1);
            }
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }
}
