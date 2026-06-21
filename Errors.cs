using System;

namespace RakNet;

public static class Errors
{
    public static readonly string ErrBufferTooSmall =
        "a message sent was larger than the buffer used to receive the message into";
    public static readonly string ErrListenerClosed = "use of closed listener";
    public static readonly string ErrNotSupported = "feature not supported";
}

/// <summary>
/// An operation error similar to net.OpError in Go, wrapping the operation name,
/// network, addresses, and inner error message.
/// </summary>
public class OpError : Exception
{
    public string Op { get; }
    public string Net { get; }
    public System.Net.IPEndPoint? Source { get; }
    public System.Net.IPEndPoint? Addr { get; }

    public OpError(string op, string net, System.Net.IPEndPoint? source,
        System.Net.IPEndPoint? addr, string msg)
        : base($"{op}: {net}: {msg}")
    {
        Op = op;
        Net = net;
        Source = source;
        Addr = addr;
    }
}
