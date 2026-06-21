using System;
using System.Collections.Concurrent;

namespace RakNet;

/// <summary>
/// Object pool for Packet instances, mirroring Go's sync.Pool. Reduces GC
/// pressure by reusing Packet objects that encapsulate content buffers.
/// Thread-safe.
/// </summary>
internal static class PacketPool
{
    private static readonly ConcurrentBag<Packet> _pool = new();

    public static Packet Get()
    {
        if (_pool.TryTake(out var pk))
            return pk;
        return new Packet { Reliability = Reliability.ReliableOrdered };
    }

    /// <summary>
    /// Returns a packet to the pool. The packet's content is cleared to avoid
    /// retaining references to large buffers.
    /// </summary>
    public static void Put(Packet pk)
    {
        pk.Content = Array.Empty<byte>();
        pk.Split = false;
        pk.SplitCount = 0;
        pk.SplitIndex = 0;
        pk.SplitId = 0;
        pk.MessageIndex = 0;
        pk.SequenceIndex = 0;
        pk.OrderIndex = 0;
        _pool.Add(pk);
    }
}
