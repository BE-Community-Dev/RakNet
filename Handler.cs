using System;
using RakNet.Messages;

namespace RakNet;

/// <summary>
/// Connection handler for the client (dialer) side. Handles RakNet-internal
/// packets received from the server, including the connection request accepted
/// handshake, connected pings/pongs, and disconnect notifications.
/// </summary>
public class DialerConnectionHandler : IConnectionHandler
{
    private readonly Action<string>? _errorLog;

    public DialerConnectionHandler(Action<string>? errorLog = null)
    {
        _errorLog = errorLog;
    }

    public bool LimitsEnabled() => false;

    public void Close(Conn conn)
    {
        try { conn.RemoteEndPoint.ToString(); } catch { }
    }

    public (bool handled, Exception? error) Handle(Conn conn, byte[] b)
    {
        if (b.Length == 0) return (false, null);
        switch (b[0])
        {
            case Id.ConnectionRequest:
                return (true, new Exception("unexpected CONNECTION_REQUEST packet"));
            case Id.ConnectionRequestAccepted:
                return (true, HandleConnectionRequestAccepted(conn, b.AsSpan(1)));
            case Id.NewIncomingConnection:
                return (true, new Exception("unexpected NEW_INCOMING_CONNECTION packet"));
            case Id.ConnectedPing:
                return (true, HandleConnectedPing(conn, b.AsSpan(1)));
            case Id.ConnectedPong:
                return (true, HandleConnectedPong(b.AsSpan(1)));
            case Id.DisconnectNotification:
                conn.CloseImmediately();
                return (true, null);
            case Id.DetectLostConnections:
                return (true, conn.SendInternal(new ConnectedPing { PingTime = Conn.Timestamp() }));
            default:
                return (false, null);
        }
    }

    private Exception? HandleConnectionRequestAccepted(Conn conn, ReadOnlySpan<byte> b)
    {
        var pk = new ConnectionRequestAccepted();
        try
        {
            pk.Unmarshal(b);
        }
        catch (Exception ex)
        {
            return new Exception("read CONNECTION_REQUEST_ACCEPTED", ex);
        }

        if (conn.IsConnected)
            return new Exception("unexpected additional CONNECTION_REQUEST_ACCEPTED packet");

        var err = conn.SendInternal(new NewIncomingConnection
        {
            ServerAddress = conn.RemoteEndPoint,
            PingTime = pk.PongTime,
            PongTime = Conn.Timestamp()
        });
        conn.SignalConnected();
        return err;
    }

    internal static Exception? HandleConnectedPing(Conn conn, ReadOnlySpan<byte> b)
    {
        var pk = new ConnectedPing();
        try
        {
            pk.Unmarshal(b);
        }
        catch (Exception ex)
        {
            return new Exception("read CONNECTED_PING", ex);
        }
        return conn.SendUnreliable(new ConnectedPong
        {
            PingTime = pk.PingTime,
            PongTime = Conn.Timestamp()
        });
    }

    internal static Exception? HandleConnectedPong(ReadOnlySpan<byte> b)
    {
        var pk = new ConnectedPong();
        try
        {
            pk.Unmarshal(b);
        }
        catch (Exception ex)
        {
            return new Exception("read CONNECTED_PONG", ex);
        }
        if (pk.PingTime > Conn.Timestamp())
            return new Exception("handle CONNECTED_PONG: timestamp is in the future");
        return null;
    }
}

/// <summary>
/// Connection handler for the server (listener) side. Handles RakNet-internal
/// packets from clients, including connection requests and the new incoming
/// connection handshake.
/// </summary>
public class ListenerConnectionHandler : IConnectionHandler
{
    private readonly Listener _listener;

    public ListenerConnectionHandler(Listener listener)
    {
        _listener = listener;
    }

    public bool LimitsEnabled() => true;

    public void Close(Conn conn)
    {
        _listener.RemoveConnection(conn.RemoteEndPoint);
    }

    public (bool handled, Exception? error) Handle(Conn conn, byte[] b)
    {
        if (b.Length == 0) return (false, null);
        switch (b[0])
        {
            case Id.ConnectionRequest:
                return (true, HandleConnectionRequest(conn, b.AsSpan(1)));
            case Id.ConnectionRequestAccepted:
                return (true, new Exception("unexpected CONNECTION_REQUEST_ACCEPTED packet"));
            case Id.NewIncomingConnection:
                return (true, HandleNewIncomingConnection(conn));
            case Id.ConnectedPing:
                return (true, DialerConnectionHandler.HandleConnectedPing(conn, b.AsSpan(1)));
            case Id.ConnectedPong:
                return (true, DialerConnectionHandler.HandleConnectedPong(b.AsSpan(1)));
            case Id.DisconnectNotification:
                conn.CloseImmediately();
                return (true, null);
            case Id.DetectLostConnections:
                return (true, conn.SendInternal(new ConnectedPing { PingTime = Conn.Timestamp() }));
            default:
                return (false, null);
        }
    }

    private Exception? HandleConnectionRequest(Conn conn, ReadOnlySpan<byte> b)
    {
        var pk = new ConnectionRequest();
        try
        {
            pk.Unmarshal(b);
        }
        catch (Exception ex)
        {
            return new Exception("read CONNECTION_REQUEST", ex);
        }
        return conn.SendInternal(new ConnectionRequestAccepted
        {
            ClientAddress = conn.RemoteEndPoint,
            PingTime = pk.RequestTime,
            PongTime = Conn.Timestamp()
        });
    }

    private Exception? HandleNewIncomingConnection(Conn conn)
    {
        if (conn.IsConnected)
            return new Exception("unexpected additional NEW_INCOMING_CONNECTION packet");
        conn.SignalConnected();
        return null;
    }
}
