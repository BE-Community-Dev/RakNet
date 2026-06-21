using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RakNet.Messages;
using MsgId = RakNet.Messages.Id;

namespace RakNet;

/// <summary>
/// Interface for a custom upstream dialer that creates a UDP socket bound to
/// a specific local address. Mirrors Go's UpstreamDialer interface, typically
/// used to bind outgoing connections to a specific local address.
/// </summary>
public interface IUpstreamDialer
{
    Socket CreateUdpSocket(IPEndPoint remoteAddr);
}

/// <summary>
/// Default upstream dialer that binds to any available local port.
/// </summary>
public class DefaultUpstreamDialer : IUpstreamDialer
{
    public Socket CreateUdpSocket(IPEndPoint remoteAddr)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return socket;
    }
}

/// <summary>
/// Upstream dialer that binds to a specific local address before connecting.
/// Equivalent to Go's net.Dialer with LocalAddr set.
/// </summary>
public class LocalAddressDialer : IUpstreamDialer
{
    private readonly IPEndPoint _localAddr;

    public LocalAddressDialer(IPEndPoint localAddr)
    {
        _localAddr = localAddr;
    }

    public Socket CreateUdpSocket(IPEndPoint remoteAddr)
    {
        var family = remoteAddr.AddressFamily == AddressFamily.InterNetworkV6
            ? AddressFamily.InterNetworkV6
            : AddressFamily.InterNetwork;
        var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(_localAddr);
        return socket;
    }
}

/// <summary>
/// Dialer provides configuration options for connecting to a RakNet server.
/// </summary>
public class Dialer
{
    public Action<string>? ErrorLog { get; set; }
    public int MaxTransientErrors { get; set; }
    public ushort MaxMTU { get; set; }

    /// <summary>
    /// UpstreamDialer overrides the default UDP socket creation. Set to a
    /// LocalAddressDialer to bind outgoing connections to a specific local
    /// address. Defaults to DefaultUpstreamDialer (binds to any port).
    /// </summary>
    public IUpstreamDialer? UpstreamDialer { get; set; }

    public TimeSpan PingTimeout => TimeSpan.FromSeconds(5);
    public TimeSpan DialTimeout => TimeSpan.FromSeconds(10);

    public byte[] Ping(string address)
    {
        return PingWithTimeout(address, PingTimeout);
    }

    public byte[] PingWithTimeout(string address, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return PingContext(cts.Token, address);
    }

    public byte[] PingContext(CancellationToken token, string address)
    {
        var endpoint = ResolveAddress(address);
        using var socket = CreateSocketFor(endpoint);
        socket.ReceiveTimeout = (int)Math.Min(5000, PingTimeout.TotalMilliseconds);

        var ping = new UnconnectedPing
        {
            PingTime = Conn.Timestamp(),
            ClientGuid = Conn.NextDialerId()
        };
        socket.SendTo(ping.Marshal(), SocketFlags.None, endpoint);

        byte[] buffer = new byte[RakNetConstants.MaxMTUSize];
        while (!token.IsCancellationRequested)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try
            {
                n = socket.ReceiveFrom(buffer, SocketFlags.None, ref remote);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (SocketException ex) when (IsTransientUDPReadError(ex))
            {
                continue;
            }
            if (n == 0 || buffer[0] != Id.UnconnectedPong)
                throw new Exception($"non-pong packet found (id = {buffer[0]})");

            var pong = new UnconnectedPong();
            pong.Unmarshal(buffer.AsSpan(1, n - 1));
            return pong.Data ?? Array.Empty<byte>();
        }
        throw new OperationCanceledException(token);
    }

    public Conn Dial(string address)
    {
        return DialTimeoutInternal(address, DialTimeout);
    }

    public Conn DialTimeoutInternal(string address, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return DialContext(cts.Token, address);
    }

    public Conn DialContext(CancellationToken token, string address)
    {
        if (MaxTransientErrors == 0) MaxTransientErrors = 10;

        var endpoint = ResolveAddress(address);
        // Socket ownership transfers to Conn — do NOT dispose here.
        var socket = CreateSocketFor(endpoint);

        var state = new ConnState
        {
            Socket = socket,
            RemoteAddr = endpoint,
            Id = Conn.NextDialerId(),
            MaxTransientErrors = MaxTransientErrors,
            MaxMTU = MaxMTU
        };

        try
        {
            state.DiscoverMTU(token);
            state.OpenConnection(token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return Connect(token, state);
    }

    private Conn Connect(CancellationToken token, ConnState state)
    {
        var conn = new Conn(state.Socket, state.RemoteAddr, state.MTU,
            new DialerConnectionHandler(ErrorLog));

        var err = conn.SendInternal(new ConnectionRequest
        {
            ClientGuid = state.Id,
            RequestTime = Conn.Timestamp()
        });
        if (err != null)
            throw new Exception("send connection request", err);

        var listenThread = new Thread(() => ClientListen(conn, state.Socket))
        {
            IsBackground = true,
            Name = "RakNet-Listen"
        };
        listenThread.Start();

        try
        {
            while (!conn.IsConnected)
            {
                if (token.IsCancellationRequested)
                {
                    conn.CloseImmediately();
                    throw new OperationCanceledException(token);
                }
                Thread.Sleep(50);
            }
            state.Socket.ReceiveTimeout = -1;
            return conn;
        }
        catch (OperationCanceledException)
        {
            conn.CloseImmediately();
            throw;
        }
    }

    private static void ClientListen(Conn rakConn, Socket socket)
    {
        byte[] buffer = new byte[RakNetConstants.MaxMTUSize];
        try
        {
            while (true)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try
                {
                    n = socket.ReceiveFrom(buffer, SocketFlags.None, ref remote);
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.TimedOut) continue;
                    if (IsTransientUDPReadError(ex)) continue;
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (n > 0)
                {
                    try
                    {
                        rakConn.Receive(buffer.AsSpan(0, n).ToArray());
                    }
                    catch (Exception)
                    {
                        // Non-fatal: RakNet recovers via retransmission or
                        // timeout-based close in the ticking thread.
                    }
                }
            }
        }
        catch (ThreadInterruptedException) { }
    }

    internal static bool IsTransientUDPReadError(SocketException ex)
    {
        return ex.SocketErrorCode is SocketError.ConnectionRefused
            or SocketError.HostUnreachable
            or SocketError.NetworkUnreachable
            or SocketError.ConnectionReset
            or SocketError.ConnectionAborted;
    }

    internal static IPEndPoint ResolveAddress(string address)
    {
        int colon = address.LastIndexOf(':');
        if (colon < 0) throw new ArgumentException($"invalid address: {address}");
        string host = address.Substring(0, colon);
        if (!int.TryParse(address.AsSpan(colon + 1), out int port))
            throw new ArgumentException($"invalid port in address: {address}");

        if (IPAddress.TryParse(host, out var ip))
            return new IPEndPoint(ip, port);

        var addresses = Dns.GetHostAddresses(host);
        if (addresses.Length == 0)
            throw new ArgumentException($"could not resolve host: {host}");
        return new IPEndPoint(addresses[0], port);
    }

    internal static Socket CreateUdpSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return socket;
    }

    internal Socket CreateSocketFor(IPEndPoint remoteAddr)
    {
        if (UpstreamDialer != null)
            return UpstreamDialer.CreateUdpSocket(remoteAddr);
        return CreateUdpSocket();
    }

    internal static ushort[] MtuSizesFor(ushort maxMTU)
    {
        const ushort minSupportedMTU = 576;
        maxMTU = Conn.ClampMTU(maxMTU, minSupportedMTU);
        ushort[] defaultSizes = { RakNetConstants.MaxMTUSize, 1200, minSupportedMTU };
        if (maxMTU == RakNetConstants.MaxMTUSize) return defaultSizes;

        List<ushort> result = new() { maxMTU };
        foreach (var s in defaultSizes)
        {
            if (s < maxMTU) result.Add(s);
        }
        return result.ToArray();
    }
}

/// <summary>
/// Internal state during the RakNet connection handshake. Holds the UDP socket,
/// remote address, discovered MTU, and security/cookie info.
/// </summary>
internal class ConnState
{
    public Socket Socket = null!;
    public IPEndPoint RemoteAddr = null!;
    public long Id;
    public ushort MTU;
    public ushort MaxMTU;
    public bool ServerSecurity;
    public uint Cookie;
    public int TransientErrorCount;
    public int MaxTransientErrors;

    private static readonly ushort[] DefaultMtuSizes = { RakNetConstants.MaxMTUSize, 1200, 576 };

    public void DiscoverMTU(CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancelToken = cts.Token;

        // Start sending request1 packets in a background thread.
        var requestThread = new Thread(() => Request1(cancelToken, Dialer.MtuSizesFor(MaxMTU)))
        {
            IsBackground = true,
            Name = "RakNet-MTU-Discovery"
        };
        requestThread.Start();

        byte[] buffer = new byte[RakNetConstants.MaxMTUSize];
        SetReadTimeout(token);

        while (true)
        {
            if (cancelToken.IsCancellationRequested)
            {
                cts.Cancel();
                throw new OperationCanceledException(token);
            }

            int n;
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                n = Socket.ReceiveFrom(buffer, SocketFlags.None, ref remote);
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.TimedOut)
                    continue;
                if (Dialer.IsTransientUDPReadError(ex) &&
                    (MaxTransientErrors == -1 || TransientErrorCount < MaxTransientErrors))
                {
                    TransientErrorCount++;
                    continue;
                }
                throw new Exception("discover MTU: read error", ex);
            }

            if (n == 0) continue;

            switch (buffer[0])
            {
                case MsgId.OpenConnectionReply1:
                    var response = new OpenConnectionReply1();
                    response.Unmarshal(buffer.AsSpan(1, n - 1));
                    ServerSecurity = response.ServerHasSecurity;
                    Cookie = response.Cookie;
                    if (response.ServerGuid == 0 || response.MTU < 400 || response.MTU > 1500)
                    {
                        OpenConnectionRequest2(response.MTU);
                        continue;
                    }
                    MTU = response.MTU;
                    cts.Cancel();
                    return;

                case MsgId.IncompatibleProtocolVersion:
                    var incProto = new IncompatibleProtocolVersion();
                    incProto.Unmarshal(buffer.AsSpan(1, n - 1));
                    cts.Cancel();
                    throw new Exception(
                        $"mismatched protocol: client protocol = {RakNetConstants.ProtocolVersion}, server protocol = {incProto.ServerProtocol}");
            }
        }
    }

    private void Request1(CancellationToken token, ushort[] sizes)
    {
        foreach (var size in sizes)
        {
            for (int j = 0; j < 4; j++)
            {
                OpenConnectionRequest1(size);
                try { Thread.Sleep(500); } catch { }
                if (token.IsCancellationRequested) return;
            }
        }
    }

    private void OpenConnectionRequest1(ushort mtu)
    {
        var pk = new OpenConnectionRequest1
        {
            ClientProtocol = RakNetConstants.ProtocolVersion,
            MTU = mtu
        };
        Socket.SendTo(pk.Marshal(), SocketFlags.None, RemoteAddr);
    }

    public void OpenConnection(CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancelToken = cts.Token;

        var requestThread = new Thread(() => Request2(cancelToken, MTU))
        {
            IsBackground = true,
            Name = "RakNet-OpenConn"
        };
        requestThread.Start();

        byte[] buffer = new byte[RakNetConstants.MaxMTUSize];
        SetReadTimeout(token);

        while (true)
        {
            if (cancelToken.IsCancellationRequested)
            {
                cts.Cancel();
                throw new OperationCanceledException(token);
            }

            int n;
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                n = Socket.ReceiveFrom(buffer, SocketFlags.None, ref remote);
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.TimedOut)
                    continue;
                if (Dialer.IsTransientUDPReadError(ex) &&
                    (MaxTransientErrors == -1 || TransientErrorCount < MaxTransientErrors))
                {
                    TransientErrorCount++;
                    continue;
                }
                throw new Exception("open connection: read error", ex);
            }

            if (n == 0) continue;
            if (buffer[0] != MsgId.OpenConnectionReply2) continue;

            var pk = new OpenConnectionReply2();
            pk.Unmarshal(buffer.AsSpan(1, n - 1));
            MTU = pk.MTU;
            cts.Cancel();
            return;
        }
    }

    private void Request2(CancellationToken token, ushort mtu)
    {
        while (true)
        {
            OpenConnectionRequest2(mtu);
            try { Thread.Sleep(500); } catch { }
            if (token.IsCancellationRequested) return;
        }
    }

    internal void OpenConnectionRequest2(ushort mtu)
    {
        var pk = new OpenConnectionRequest2
        {
            ServerAddress = RemoteAddr,
            MTU = mtu,
            ClientGuid = Id,
            ServerHasSecurity = ServerSecurity,
            Cookie = Cookie
        };
        Socket.SendTo(pk.Marshal(), SocketFlags.None, RemoteAddr);
    }

    private void SetReadTimeout(CancellationToken token)
    {
        // Use a 500ms receive timeout so we can periodically check the
        // cancellation token for overall deadline expiry.
        Socket.ReceiveTimeout = 500;
    }
}
