using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace RakNet;

/// <summary>
/// DoS protection layer for a RakNet listener. Tracks IP addresses that have
/// been blocked due to protocol errors and automatically unblocks them after
/// the block duration expires. Also manages periodic rotation of the cookie
/// salt used to prevent IP spoofing during the connection handshake.
/// </summary>
internal sealed class Security
{
    private readonly ListenConfig _conf;
    private readonly Listener _listener;
    private int _blockCount;
    private readonly object _mu = new();
    private readonly Dictionary<string, DateTime> _blocks = new();

    public Security(ListenConfig conf, Listener listener)
    {
        _conf = conf;
        _listener = listener;
    }

    /// <summary>
    /// Runs a background tick that garbage-collects expired blocks every
    /// second and rotates the cookie salt every 2 seconds. Runs until the
    /// stop token is cancelled.
    /// </summary>
    public void Tick(CancellationToken stop)
    {
        int i = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                if (stop.IsCancellationRequested) return;
                GcBlocks();
                if (++i % 2 == 0)
                {
                    // Rotate salt: current becomes previous, new current generated.
                    _listener._previousSalt = _listener._cookieSalt;
                    _listener._cookieSalt = (ulong)Random.Shared.NextInt64();
                }
            }
        }
        catch (ThreadInterruptedException) { }
    }

    /// <summary>
    /// Blocks the IP of the given address for the listener's configured
    /// BlockDuration.
    /// </summary>
    public void Block(IPEndPoint addr) => BlockFor(addr, _conf.BlockDuration);

    /// <summary>
    /// Blocks the IP of the given address for the specified duration. No-op
    /// if duration is zero or negative.
    /// </summary>
    public void BlockFor(IPEndPoint addr, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        string ipKey = addr.Address.ToString();
        lock (_mu)
        {
            if (!_blocks.ContainsKey(ipKey))
                Interlocked.Increment(ref _blockCount);
            _blocks[ipKey] = DateTime.Now + duration;
        }
    }

    /// <summary>
    /// Checks if the IP of the given address is currently blocked. Uses a
    /// fast-path atomic check to avoid lock contention when no blocks exist.
    /// </summary>
    public bool Blocked(IPEndPoint addr)
    {
        if (Interlocked.CompareExchange(ref _blockCount, 0, 0) == 0)
            return false;

        string ipKey = addr.Address.ToString();
        lock (_mu)
        {
            if (!_blocks.TryGetValue(ipKey, out var expiresAt))
                return false;
            if (DateTime.Now >= expiresAt)
            {
                _blocks.Remove(ipKey);
                Interlocked.Exchange(ref _blockCount, _blocks.Count);
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Removes expired blocks from the map. Only runs cleanup when there are
    /// active blocks.
    /// </summary>
    private void GcBlocks()
    {
        if (Interlocked.CompareExchange(ref _blockCount, 0, 0) == 0)
            return;
        var now = DateTime.Now;
        lock (_mu)
        {
            List<string> toRemove = new();
            foreach (var kvp in _blocks)
            {
                if (now >= kvp.Value)
                    toRemove.Add(kvp.Key);
            }
            foreach (var key in toRemove)
                _blocks.Remove(key);
            Interlocked.Exchange(ref _blockCount, _blocks.Count);
        }
    }
}
