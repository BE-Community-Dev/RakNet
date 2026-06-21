using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RakNet.Messages;

namespace RakNet;

/// <summary>
/// Configuration options for a RakNet listener.
/// </summary>
public class ListenConfig
{
    public Action<string>? ErrorLog { get; set; }
    public bool DisableCookies { get; set; }
    public ushort MaxMTU { get; set; }
    public TimeSpan BlockDuration { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// A RakNet server listener. Listens for incoming connections on a UDP port,
/// handles the unconnected handshake (with cookie security and DoS protection),
/// and provides accepted connections via <see cref="Accept"/>.
/// </summary>
public class Listener : IDisposable
{
    internal readonly ListenConfig _conf;
    private readonly Socket _udp;
    private readonly Security _security;
    internal readonly ConcurrentDictionary<string, Conn> _connections = new();
    internal readonly BlockingCollection<Conn> _incoming = new();
    private readonly CancellationTokenSource _cts = new();
    private int _closed;

    internal ulong _cookieSalt;
    internal ulong _previousSalt;

    public long Id { get; }

    private byte[]? _pongData;
    private Func<IPEndPoint, byte[]>? _pongDataFunc;

    private static long _listenerId = Random.Shared.NextInt64();

    internal Listener(ListenConfig conf, Socket udp)
    {
        _conf = conf;
        _udp = udp;
        Id = Interlocked.Increment(ref _listenerId);
        _cookieSalt = (ulong)Random.Shared.NextInt64();
        _previousSalt = (ulong)Random.Shared.NextInt64();
        _security = new Security(conf, this);
    }

    public static Listener Listen(string address)
    {
        return new ListenConfig().Listen(address);
    }

    /// <summary>
    /// Blocks until a connection can be accepted. Returns the accepted Conn.
    /// Throws OperationCanceledException if the listener is closed.
    /// </summary>
    public Conn Accept()
    {
        return _incoming.Take(_cts.Token);
    }

    public IPEndPoint LocalEndPoint => (_udp.LocalEndPoint as IPEndPoint)!;

    /// <summary>
    /// Blocks packets from the given address for the listener's configured
    /// BlockDuration.
    /// </summary>
    public void Block(IPEndPoint addr) => _security.Block(addr);

    /// <summary>
    /// Blocks packets from the given address for the specified duration.
    /// </summary>
    public void BlockFor(IPEndPoint addr, TimeSpan duration) => _security.BlockFor(addr, duration);

    internal void RemoveConnection(IPEndPoint addr)
    {
        _connections.TryRemove(addr.ToString(), out _);
    }

    /// <summary>
    /// Sets the static pong data returned to all unconnected pings. Panics
    /// if the data exceeds 32767 bytes (MaxInt16).
    /// </summary>
    public void SetPongData(byte[] data)
    {
        if (data.Length > short.MaxValue)
            throw new ArgumentException($"pong data: must be no longer than {short.MaxValue} bytes, got {data.Length}");
        _pongData = data;
    }

    /// <summary>
    /// Sets a function that generates pong data dynamically per-address.
    /// Takes priority over static pong data. Pass null to revert to static.
    /// </summary>
    public void SetPongDataFunc(Func<IPEndPoint, byte[]>? f)
    {
        _pongDataFunc = f;
    }

    /// <summary>
    /// Returns the pong data for a given address, preferring the dynamic
    /// function if set.
    /// </summary>
    internal byte[] GetPongData(IPEndPoint addr)
    {
        if (_pongDataFunc != null)
        {
            var data = _pongDataFunc(addr);
            if (data.Length > short.MaxValue)
                throw new InvalidOperationException($"pong data func: data must be no longer than {short.MaxValue} bytes, got {data.Length}");
            return data;
        }
        return _pongData ?? Array.Empty<byte>();
    }

    public void Close()
    {
        if (Interlocked.CompareExchange(ref _closed, 1, 0) != 0) return;
        _cts.Cancel();
        try { _udp.Close(); } catch { }
        _incoming.CompleteAdding();
    }

    public void Dispose() => Close();

    internal uint ComputeCookie(IPEndPoint addr, ulong salt)
    {
        if (_conf.DisableCookies) return 0;
        byte[] b = new byte[26];
        Binary.PutUint64LE(b, salt);
        Binary.PutUint16(b.AsSpan(8), (ushort)addr.Port);
        byte[] ip = addr.Address.GetAddressBytes();
        Array.Copy(ip, 0, b, 10, ip.Length);
        return Crc32.Compute(b);
    }

    internal bool IsBlocked(IPEndPoint addr) => _security.Blocked(addr);
    internal void SecurityBlock(IPEndPoint addr) => _security.Block(addr);
    internal CancellationToken ClosedToken => _cts.Token;
}

public static class ListenConfigExtensions
{
    public static Listener Listen(this ListenConfig conf, string address)
    {
        if (conf.ErrorLog == null) conf.ErrorLog = _ => { };
        conf.MaxMTU = Conn.ClampMTU(conf.MaxMTU, RakNetConstants.MinMTUSize);

        int colon = address.LastIndexOf(':');
        string host = address.Substring(0, colon);
        int port = int.Parse(address.Substring(colon + 1));
        var ip = IPAddress.TryParse(host, out var addr) ? addr : IPAddress.Any;
        var endpoint = new IPEndPoint(ip, port);

        var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(endpoint);

        var listener = new Listener(conf, udp);

        var listenThread = new Thread(() => ListenLoop(listener, udp, conf))
        {
            IsBackground = true,
            Name = "RakNet-Listener"
        };
        listenThread.Start();

        var securityThread = new Thread(() => listener.RunSecurity())
        {
            IsBackground = true,
            Name = "RakNet-Security"
        };
        securityThread.Start();

        return listener;
    }

    internal static void RunSecurity(this Listener listener)
    {
        var security = new Security(listener._conf, listener);
        security.Tick(listener.ClosedToken);
    }

    private static void ListenLoop(Listener listener, Socket udp, ListenConfig conf)
    {
        byte[] buffer = new byte[1500];
        try
        {
            while (true)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try
                {
                    n = udp.ReceiveFrom(buffer, SocketFlags.None, ref remote);
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (n == 0) continue;
                var ep = (IPEndPoint)remote;

                if (listener.IsBlocked(ep))
                    continue;

                if (listener._connections.TryGetValue(ep.ToString(), out var conn))
                {
                    if (conn.ContextToken.IsCancellationRequested)
                        continue;
                    try
                    {
                        conn.Receive(buffer.AsSpan(0, n).ToArray());
                    }
                    catch (Exception ex)
                    {
                        conf.ErrorLog?.Invoke($"handle packet: {ex.Message}");
                        listener.SecurityBlock(ep);
                    }
                    continue;
                }

                try
                {
                    HandleUnconnected(listener, udp, buffer.AsSpan(0, n), ep, conf);
                }
                catch (Exception ex)
                {
                    conf.ErrorLog?.Invoke($"handle packet: {ex.Message} (raddr={ep}, block-duration={Math.Max(0, (int)conf.BlockDuration.TotalSeconds)})");
                    listener.SecurityBlock(ep);
                }
            }
        }
        catch (ThreadInterruptedException) { }
    }

    private static void HandleUnconnected(Listener listener, Socket udp,
        ReadOnlySpan<byte> b, IPEndPoint addr, ListenConfig conf)
    {
        if (b.Length == 0) return;
        switch (b[0])
        {
            case Id.UnconnectedPing:
            case Id.UnconnectedPingOpenConnections:
                HandleUnconnectedPing(listener, udp, b.Slice(1), addr);
                break;
            case Id.OpenConnectionRequest1:
                HandleOpenConnectionRequest1(listener, udp, b.Slice(1), addr, conf);
                break;
            case Id.OpenConnectionRequest2:
                HandleOpenConnectionRequest2(listener, udp, b.Slice(1), addr, conf);
                break;
            default:
                if ((b[0] & RakNetConstants.BitFlagDatagram) != 0)
                {
                    conf.ErrorLog?.Invoke($"unexpected datagram (raddr={addr})");
                    return;
                }
                throw new Exception($"unknown unconnected packet (id={b[0]:x}, len={b.Length})");
        }
    }

    private static void HandleUnconnectedPing(Listener listener, Socket udp,
        ReadOnlySpan<byte> b, IPEndPoint addr)
    {
        var pk = new UnconnectedPing();
        pk.Unmarshal(b);
        var pong = new UnconnectedPong
        {
            ServerGuid = listener.Id,
            PingTime = pk.PingTime,
            Data = listener.GetPongData(addr)
        };
        udp.SendTo(pong.Marshal(), SocketFlags.None, addr);
    }

    private static void HandleOpenConnectionRequest1(Listener listener, Socket udp,
        ReadOnlySpan<byte> b, IPEndPoint addr, ListenConfig conf)
    {
        var pk = new OpenConnectionRequest1();
        pk.Unmarshal(b);
        ushort mtuSize = Math.Min(pk.MTU, conf.MaxMTU);

        if (pk.ClientProtocol != RakNetConstants.ProtocolVersion)
        {
            var incProto = new IncompatibleProtocolVersion
            {
                ServerProtocol = RakNetConstants.ProtocolVersion,
                ServerGuid = listener.Id
            };
            udp.SendTo(incProto.Marshal(), SocketFlags.None, addr);
            throw new Exception($"handle OPEN_CONNECTION_REQUEST_1: incompatible protocol version {pk.ClientProtocol} (listener protocol = {RakNetConstants.ProtocolVersion})");
        }

        var reply = new OpenConnectionReply1
        {
            ServerGuid = listener.Id,
            Cookie = listener.ComputeCookie(addr, listener._cookieSalt),
            ServerHasSecurity = !conf.DisableCookies,
            MTU = mtuSize
        };
        udp.SendTo(reply.Marshal(), SocketFlags.None, addr);
    }

    private static void HandleOpenConnectionRequest2(Listener listener, Socket udp,
        ReadOnlySpan<byte> b, IPEndPoint addr, ListenConfig conf)
    {
        var pk = new OpenConnectionRequest2 { ServerHasSecurity = !conf.DisableCookies };
        pk.Unmarshal(b);

        // Validate cookie against current and previous salt.
        if (!conf.DisableCookies)
        {
            uint expected = listener.ComputeCookie(addr, listener._cookieSalt);
            uint prevExpected = listener.ComputeCookie(addr, listener._previousSalt);
            if (pk.Cookie != expected && pk.Cookie != prevExpected)
                throw new Exception($"handle OPEN_CONNECTION_REQUEST_2: invalid cookie '{pk.Cookie:X}', expected '{expected:X}'");
        }

        // Vanilla clients always provide a negative ClientGUID.
        if (pk.ClientGuid >= 0)
            throw new Exception($"handle OPEN_CONNECTION_REQUEST_2: invalid ClientGUID '{pk.ClientGuid}', expected negative");

        ushort mtuSize = Math.Min(pk.MTU, conf.MaxMTU);

        var reply = new OpenConnectionReply2
        {
            ServerGuid = listener.Id,
            ClientAddress = addr,
            MTU = mtuSize
        };
        udp.SendTo(reply.Marshal(), SocketFlags.None, addr);

        var conn = new Conn(udp, addr, mtuSize, new ListenerConnectionHandler(listener), ownsSocket: false);
        listener._connections[addr.ToString()] = conn;

        // Spawn a goroutine-equivalent that waits for the connection to
        // complete (NewIncomingConnection) or times out after 10 seconds.
        new Thread(() =>
        {
            try
            {
                using var timer = new Timer(_ =>
                {
                    if (!conn.IsConnected)
                    {
                        conf.ErrorLog?.Invoke($"connection from {addr} timed out, closing");
                        conn.Close();
                    }
                }, null, 10000, Timeout.Infinite);

                while (!conn.IsConnected && !conn.ContextToken.IsCancellationRequested)
                {
                    Thread.Sleep(50);
                }

                if (conn.IsConnected)
                {
                    listener._incoming.Add(conn);
                }
            }
            catch (InvalidOperationException) { }
            catch (OperationCanceledException) { }
        }) { IsBackground = true }.Start();
    }
}
