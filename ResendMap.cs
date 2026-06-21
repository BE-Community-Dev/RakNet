using System;
using System.Collections.Generic;

namespace RakNet;

/// <summary>
/// A record of a sent datagram kept for potential retransmission. Stores the
/// packet and the timestamp it was originally sent.
/// </summary>
public class ResendRecord
{
    public Packet Packet;
    public DateTime Timestamp;
}

/// <summary>
/// Tracks unacknowledged sent datagrams for retransmission, and maintains a
/// sliding window of round-trip time (RTT) measurements for adaptive timeout
/// calculation.
/// </summary>
public class ResendMap
{
    public Dictionary<uint, ResendRecord> Unacknowledged = new();
    private readonly Dictionary<DateTime, TimeSpan> _delays = new();

    private static readonly TimeSpan RttCalculationWindow = TimeSpan.FromSeconds(5);

    public void Add(uint index, Packet pk)
    {
        Unacknowledged[index] = new ResendRecord { Packet = pk, Timestamp = DateTime.Now };
    }

    /// <summary>
    /// Marks a packet as acknowledged. The packet is removed and its RTT is
    /// recorded once (multiplier 1).
    /// </summary>
    public (Packet?, bool) Acknowledge(uint index) => Remove(index, 1);

    /// <summary>
    /// Looks up a packet for retransmission. The packet is removed and its RTT
    /// is recorded with multiplier 2 (since retransmission means the original
    /// send + the retransmit round trip).
    /// </summary>
    public (Packet?, bool) Retransmit(uint index) => Remove(index, 2);

    private (Packet?, bool) Remove(uint index, int mul)
    {
        if (!Unacknowledged.TryGetValue(index, out var record))
            return (null, false);
        Unacknowledged.Remove(index);

        var now = DateTime.Now;
        _delays[now] = now - record.Timestamp;
        _delays[now] *= mul;
        return (record.Packet, true);
    }

    /// <summary>
    /// Returns the average round-trip time over the last 5 seconds of
    /// measurements. Falls back to 50ms if no measurements are available.
    /// </summary>
    public TimeSpan Rtt(DateTime now)
    {
        // Remove records older than the window.
        List<DateTime> toRemove = new();
        foreach (var kvp in _delays)
        {
            if (now - kvp.Key > RttCalculationWindow)
                toRemove.Add(kvp.Key);
        }
        foreach (var t in toRemove) _delays.Remove(t);

        if (_delays.Count == 0) return TimeSpan.FromMilliseconds(50);

        TimeSpan total = TimeSpan.Zero;
        foreach (var rtt in _delays.Values)
            total += rtt;
        return total / _delays.Count;
    }
}
