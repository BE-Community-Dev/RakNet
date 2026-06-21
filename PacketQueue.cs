using System;
using System.Collections.Generic;

namespace RakNet;

/// <summary>
/// An ordered queue for reliable-ordered packets. Packets are inserted at their
/// order index and can only be fetched in sequence — a gap blocks all higher
/// indices until the missing one arrives.
/// </summary>
public class PacketQueue
{
    public uint Lowest;
    public uint Highest;
    private readonly Dictionary<uint, byte[]> _queue = new();

    /// <summary>
    /// Puts a packet at the given index. Returns false if the index was already
    /// occupied or is below the current lowest (duplicate).
    /// </summary>
    public bool Put(uint index, byte[] packet)
    {
        if (index < Lowest) return false;
        if (_queue.ContainsKey(index)) return false;
        if (index >= Highest) Highest = index + 1;
        _queue[index] = packet;
        return true;
    }

    /// <summary>
    /// Fetches as many consecutive packets as possible starting from Lowest.
    /// Stops at the first gap and advances Lowest.
    /// </summary>
    public List<byte[]> Fetch()
    {
        List<byte[]> packets = new();
        uint index = Lowest;
        while (index < Highest)
        {
            if (!_queue.TryGetValue(index, out var packet)) break;
            _queue.Remove(index);
            packets.Add(packet);
            index++;
        }
        Lowest = index;
        return packets;
    }

    public uint WindowSize() => Highest - Lowest;
}
