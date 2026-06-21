using System;
using System.Collections.Concurrent;
using System.Threading;

namespace RakNet;

/// <summary>
/// A thread-safe channel for byte[] packets that grows its internal buffer
/// capacity when full, up to a maximum limit. Mirrors Go's ElasticChan[[]byte].
///
/// Single-producer, multi-consumer safe. Send never blocks — if the buffer
/// is full and below the max limit, the buffer is doubled. If at the max
/// limit, Send spins briefly rather than blocking forever.
/// </summary>
internal sealed class ElasticChan
{
    private readonly int _maxCapacity;
    private BlockingCollection<byte[]> _channel;
    private int _currentCapacity;

    public ElasticChan(int initialCapacity, int maxCapacity)
    {
        _maxCapacity = maxCapacity;
        _currentCapacity = initialCapacity;
        _channel = new BlockingCollection<byte[]>(initialCapacity);
    }

    /// <summary>
    /// Sends a value. Never blocks — grows the channel if full and below max.
    /// </summary>
    public void Send(byte[] value)
    {
        while (true)
        {
            try
            {
                _channel.Add(value);
                return;
            }
            catch (InvalidOperationException) when (_channel.IsAddingCompleted)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Receives a value, blocking until one is available or the token cancels.
    /// Returns false if cancelled.
    /// </summary>
    public bool Recv(CancellationToken token, out byte[]? value)
    {
        try
        {
            value = _channel.Take(token);
            return true;
        }
        catch (OperationCanceledException)
        {
            value = null;
            return false;
        }
        catch (InvalidOperationException) when (_channel.IsCompleted)
        {
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Completes adding, causing consumers to stop blocking once the queue
    /// drains.
    /// </summary>
    public void CompleteAdding() => _channel.CompleteAdding();
}
