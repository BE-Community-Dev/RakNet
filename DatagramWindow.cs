using System;
using System.Collections.Generic;

namespace RakNet;

/// <summary>
/// A sliding window for tracking incoming datagram sequence numbers. Used to
/// detect duplicates, identify missing datagrams (for NACKs), and maintain the
/// expected ordering.
/// </summary>
public class DatagramWindow
{
    public uint Lowest;
    public uint Highest;
    private readonly Dictionary<uint, DateTime> _queue = new();

    /// <summary>
    /// Adds a datagram sequence number to the window. Returns false if the
    /// sequence number was already seen (duplicate).
    /// </summary>
    public bool Add(uint index)
    {
        if (Seen(index)) return false;
        Highest = Math.Max(Highest, index + 1);
        _queue[index] = DateTime.Now;
        return true;
    }

    /// <summary>
    /// Checks if the index has already been received.
    /// </summary>
    public bool Seen(uint index)
    {
        if (index < Lowest) return true;
        return _queue.ContainsKey(index);
    }

    /// <summary>
    /// Removes consecutive indices from the queue starting at Lowest, advancing
    /// Lowest past all present entries. Returns the number removed.
    /// </summary>
    public int Shift()
    {
        int n = 0;
        uint index;
        for (index = Lowest; index < Highest; index++)
        {
            if (!_queue.ContainsKey(index)) break;
            _queue.Remove(index);
            n++;
        }
        Lowest = index;
        return n;
    }

    /// <summary>
    /// Returns all missing indices in the window that have been pending longer
    /// than the given duration. The queue is shifted after this call.
    /// </summary>
    public List<uint> Missing(TimeSpan since)
    {
        List<uint> indices = new();
        bool missing = false;
        for (int index = (int)Highest - 1; index >= (int)Lowest; index--)
        {
            uint i = (uint)index;
            if (_queue.TryGetValue(i, out var t))
            {
                if (DateTime.Now - t >= since)
                    missing = true;
                continue;
            }
            if (missing)
            {
                indices.Add(i);
                _queue[i] = default;
            }
        }
        Shift();
        return indices;
    }

    /// <summary>
    /// Returns the size of the window (highest - lowest).
    /// </summary>
    public uint Size() => Highest - Lowest;
}
