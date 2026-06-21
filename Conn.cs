using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RakNet.Messages;

namespace RakNet;

/// <summary>
/// The connection handler interface. Implementations handle RakNet-internal
/// packets (pings, connection requests, etc.) and provide hooks for closing
/// and logging.
/// </summary>
public interface IConnectionHandler
{
    (bool handled, Exception? error) Handle(Conn conn, byte[] b);
    bool LimitsEnabled();
    void Close(Conn conn);
}

/// <summary>
/// Represents a RakNet connection to a specific remote endpoint. Although UDP
/// is connectionless, this emulates a reliable connection using the RakNet
/// protocol with ACKs, NACKs, retransmission, and ordered packet delivery.
/// </summary>
public class Conn : IDisposable
{
    private readonly Socket _udp;
    private readonly IPEndPoint _raddr;
    private readonly IConnectionHandler _handler;
    private readonly bool _ownsSocket;

    private readonly object _mu = new();
    private readonly object _ackMu = new();

    private long _closing; // 0 = open, >0 = unix timestamp when Close() was called
    private long _rttTicks; // last measured RTT in ticks
    private long _lastActivityTicks;

    private readonly CancellationTokenSource _cts = new();

    private readonly MemoryStream _buf;
    private readonly MemoryStream _ackBuf;
    private readonly MemoryStream _nackBuf;

    private readonly Packet _pk = new();

    private uint _seq, _orderIndex, _messageIndex; // uint24 counters
    private uint _splitId; // uint32

    private readonly ushort _mtu;
    private readonly Dictionary<ushort, byte[]?[]> _splits = new();
    private readonly DatagramWindow _win = new();
    private readonly List<uint> _ackSlice = new();
    private readonly PacketQueue _packetQueue = new();
    private readonly ElasticChan _packets = new(4, 4096);
    private readonly ResendMap _retransmission = new();

    private readonly ManualResetEventSlim _connected = new();
    private int _closeOnce;

    private static readonly DateTime StartTime = DateTime.Now;

    private static long _dialerId = -Random.Shared.NextInt64();

    public Conn(Socket udp, IPEndPoint raddr, ushort mtu, IConnectionHandler handler, bool ownsSocket = true)
    {
        _mtu = ClampMTU(mtu, RakNetConstants.MinMTUSize);
        _udp = udp;
        _raddr = raddr;
        _handler = handler;
        _ownsSocket = ownsSocket;
        _buf = new MemoryStream(_mtu - 28);
        _ackBuf = new MemoryStream(128);
        _nackBuf = new MemoryStream(64);
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.Now.Ticks);
        var tickThread = new Thread(StartTicking) { IsBackground = true, Name = "RakNet-Tick" };
        tickThread.Start();
    }

    public static ushort ClampMTU(ushort mtu, ushort minMTU)
    {
        if (mtu == 0 || mtu > RakNetConstants.MaxMTUSize) return RakNetConstants.MaxMTUSize;
        return Math.Max(mtu, minMTU);
    }

    public ushort EffectiveMTU() => (ushort)(_mtu - 28);

    public IPEndPoint RemoteEndPoint => _raddr;
    public IPEndPoint? LocalEndPoint => _udp.LocalEndPoint as IPEndPoint;

    public TimeSpan Latency => TimeSpan.FromTicks(Interlocked.Read(ref _rttTicks) / 2);

    public CancellationToken ContextToken => _cts.Token;

    public bool IsConnected => _connected.IsSet;

    internal void SignalConnected() => _connected.Set();

    /// <summary>
    /// Returns a timestamp in milliseconds since the process started, matching
    /// the Go implementation's timestamp() function.
    /// </summary>
    public static long Timestamp() => (long)(DateTime.Now - StartTime).TotalMilliseconds;

    private void StartTicking()
    {
        var interval = TimeSpan.FromMilliseconds(100);
        long i = 0;
        int prevAcksLeft = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                Thread.Sleep(interval);
                var now = DateTime.Now;
                i++;
                FlushACKs();
                if (i % 3 == 0)
                    CheckResend(now);

                long closingTicks = Interlocked.Read(ref _closing);
                if (closingTicks != 0)
                {
                    int acksLeft;
                    lock (_mu)
                    {
                        acksLeft = _retransmission.Unacknowledged.Count;
                    }

                    if (prevAcksLeft != 0 && acksLeft == 0)
                    {
                        CloseImmediately();
                        continue;
                    }
                    prevAcksLeft = acksLeft;

                    var since = now - new DateTime(closingTicks, DateTimeKind.Local);
                    if ((acksLeft == 0 && since > TimeSpan.FromSeconds(1)) || since > TimeSpan.FromSeconds(5))
                    {
                        CloseImmediately();
                        continue;
                    }
                    continue;
                }

                if (i % 5 == 0)
                {
                    _ = SendInternal(new ConnectedPing { PingTime = Timestamp() });

                    lock (_mu)
                    {
                        var lastActivity = new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Local);
                        var rtt = _retransmission.Rtt(now);
                        if (now - lastActivity > TimeSpan.FromSeconds(5) + rtt * 2)
                        {
                            Close();
                        }
                    }
                }
            }
        }
        catch (ThreadInterruptedException) { }
    }

    private void FlushACKs()
    {
        lock (_ackMu)
        {
            if (_ackSlice.Count > 0)
            {
                if (SendACK(_ackSlice))
                {
                    _ackSlice.Clear();
                }
            }
        }
    }

    private void CheckResend(DateTime now)
    {
        lock (_mu)
        {
            List<uint> resend = new();
            var rtt = _retransmission.Rtt(now);
            var delay = rtt + rtt / 2;
            Interlocked.Exchange(ref _rttTicks, rtt.Ticks);

            foreach (var kvp in _retransmission.Unacknowledged)
            {
                if (now - kvp.Value.Timestamp > delay)
                {
                    resend.Add(kvp.Key);
                }
            }
            _ = Resend(resend);
        }
    }

    /// <summary>
    /// Writes data to the connection using ReliableOrdered reliability.
    /// </summary>
    public int Write(byte[] b)
    {
        return WriteWithReliability(b, Reliability.ReliableOrdered);
    }

    public int WriteWithReliability(byte[] b, Reliability rel)
    {
        if (_cts.IsCancellationRequested)
            throw new ObjectDisposedException("Conn", "connection is closed");

        lock (_mu)
        {
            return WriteInternal(b, rel);
        }
    }

    private int WriteInternal(byte[] b, Reliability rel)
    {
        byte[][] fragments = PacketSplitter.Split(b, EffectiveMTU());
        uint orderIndex = 0;
        if (rel.SequencedOrOrdered())
        {
            orderIndex = _orderIndex;
            _orderIndex = (_orderIndex + 1) & Binary.Uint24Mask;
        }

        ushort splitId = (ushort)(_splitId & 0xFFFF);
        if (fragments.Length > 1)
        {
            _splitId++;
        }

        int n = 0;
        for (int splitIndex = 0; splitIndex < fragments.Length; splitIndex++)
        {
            var pk = PacketPool.Get();
            pk.Content = fragments[splitIndex];
            pk.OrderIndex = orderIndex;
            pk.Reliability = rel;
            if (rel.Reliable())
            {
                pk.MessageIndex = _messageIndex;
                _messageIndex = (_messageIndex + 1) & Binary.Uint24Mask;
            }
            pk.Split = fragments.Length > 1;
            if (pk.Split)
            {
                pk.SplitCount = (uint)fragments.Length;
                pk.SplitIndex = (uint)splitIndex;
                pk.SplitId = splitId;
            }
            if (SendDatagram(pk) is { } ex)
                throw ex;
            n += fragments[splitIndex].Length;
        }
        return n;
    }

    /// <summary>
    /// Reads the next packet from the connection. Blocks until a packet is
    /// available or the connection is closed.
    /// </summary>
    public byte[] ReadPacket()
    {
        if (_packets.Recv(_cts.Token, out var pk) && pk != null)
            return pk;
        throw new ObjectDisposedException("Conn", "connection is closed");
    }

    public int Read(byte[] buffer)
    {
        byte[] pk = ReadPacket();
        if (buffer.Length < pk.Length)
            throw new InvalidOperationException(Errors.ErrBufferTooSmall);
        Array.Copy(pk, buffer, pk.Length);
        return pk.Length;
    }

    /// <summary>
    /// Gracefully closes the connection. Sends a disconnect notification once
    /// all pending data is acknowledged, then closes the socket.
    /// </summary>
    public void Close()
    {
        long now = DateTime.Now.Ticks;
        Interlocked.CompareExchange(ref _closing, now, 0);
    }

    public void Dispose()
    {
        Close();
        // Force immediate close if still pending
        CloseImmediately();
    }

    internal void CloseImmediately()
    {
        if (Interlocked.CompareExchange(ref _closeOnce, 1, 0) != 0) return;
        try
        {
            _ = Write(new byte[] { Id.DisconnectNotification });
        }
        catch { }
        _handler.Close(this);
        _cts.Cancel();
        _connected.Set();

        // Return all unacknowledged packets to the pool and clear the map.
        lock (_mu)
        {
            foreach (var record in _retransmission.Unacknowledged.Values)
            {
                PacketPool.Put(record.Packet);
            }
            _retransmission.Unacknowledged.Clear();
        }

        // Only close the socket if this Conn owns it (client side). The
        // listener-side Conn shares the listener's UDP socket.
        if (_ownsSocket)
        {
            try { _udp.Close(); } catch { }
        }
        _packets.CompleteAdding();
    }

    internal Exception? SendInternal(IMarshalable pk)
    {
        byte[] data = pk.Marshal();
        try
        {
            Write(data);
        }
        catch (Exception ex)
        {
            return ex;
        }
        return null;
    }

    internal Exception? SendUnreliable(IMarshalable pk)
    {
        byte[] data = pk.Marshal();
        try
        {
            WriteWithReliability(data, Reliability.Unreliable);
        }
        catch (Exception ex)
        {
            return ex;
        }
        return null;
    }

    /// <summary>
    /// Called by the listen loop when raw UDP data arrives. Dispatches based on
    /// the frame type (ACK, NACK, or datagram).
    /// </summary>
    public void Receive(byte[] b)
    {
        if (b.Length == 0) return;
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.Now.Ticks);

        if ((b[0] & RakNetConstants.BitFlagAck) != 0)
        {
            HandleACK(b.AsSpan(1));
        }
        else if ((b[0] & RakNetConstants.BitFlagNack) != 0)
        {
            HandleNACK(b.AsSpan(1));
        }
        else if ((b[0] & RakNetConstants.BitFlagDatagram) != 0)
        {
            ReceiveDatagram(b.AsSpan(1));
        }
    }

    private void ReceiveDatagram(ReadOnlySpan<byte> b)
    {
        if (b.Length < 3) throw new EndOfStreamException("read datagram: unexpected EOF");
        uint seq = Binary.LoadUint24(b);
        if (!_win.Add(seq))
            return;

        lock (_ackMu)
        {
            _ackSlice.Add(seq);
        }

        if (_win.Shift() == 0)
        {
            var rtt = TimeSpan.FromTicks(Interlocked.Read(ref _rttTicks));
            var missing = _win.Missing(rtt + rtt / 2);
            if (missing.Count > 0)
            {
                if (SendNACK(missing) is { } ex)
                    throw new Exception("receive datagram: send NACK", ex);
            }
        }

        if (_win.Size() > RakNetConstants.MaxWindowSize && _handler.LimitsEnabled())
            throw new Exception($"receive datagram: queue window size is too big ({_win.Lowest}-{_win.Highest})");

        HandleDatagram(b.Slice(3));
    }

    private void HandleDatagram(ReadOnlySpan<byte> b)
    {
        while (b.Length > 0)
        {
            int n;
            try
            {
                n = _pk.Read(b);
            }
            catch (Exception ex)
            {
                throw new Exception("handle datagram: read packet", ex);
            }
            b = b.Slice(n);

            Exception? err;
            if (_pk.Split)
                err = ReceiveSplitPacket(_pk);
            else
                err = ReceivePacket(_pk);
            if (err != null)
                throw new Exception("handle datagram: receive packet", err);
        }
    }

    private Exception? ReceivePacket(Packet packet)
    {
        if (packet.Reliability != Reliability.ReliableOrdered)
        {
            return HandlePacket(packet.Content);
        }
        if (!_packetQueue.Put(packet.OrderIndex, packet.Content))
            return null;

        if (_packetQueue.WindowSize() > RakNetConstants.MaxWindowSize && _handler.LimitsEnabled())
            return new Exception($"packet queue window size is too big ({_packetQueue.Lowest}-{_packetQueue.Highest})");

        foreach (var content in _packetQueue.Fetch())
        {
            var err = HandlePacket(content);
            if (err != null) return err;
        }
        return null;
    }

    private Exception? HandlePacket(byte[] b)
    {
        if (b.Length == 0)
            return new Exception("handle packet: zero packet length");
        if (Interlocked.Read(ref _closing) != 0)
            return null;

        var (handled, err) = _handler.Handle(this, b);
        if (err != null)
            return new Exception("handle packet", err);
        if (!handled)
        {
            _packets.Send(b);
        }
        return null;
    }

    private Exception? ReceiveSplitPacket(Packet p)
    {
        const int maxSplitCount = 512;
        const int maxConcurrentSplits = 16;

        if (p.SplitCount > maxSplitCount && _handler.LimitsEnabled())
            return new Exception($"split packet: split count {p.SplitCount} exceeds max {maxSplitCount}");
        if (_splits.Count > maxConcurrentSplits && _handler.LimitsEnabled())
            return new Exception($"split packet: max concurrent splits {maxConcurrentSplits} reached");

        if (!_splits.TryGetValue((ushort)p.SplitId, out var m))
        {
            m = new byte[]?[p.SplitCount];
            _splits[(ushort)p.SplitId] = m;
        }
        if (p.SplitIndex > m.Length - 1)
            return new Exception($"split packet: split index {p.SplitIndex} out of range (0-{m.Length - 1})");
        m[p.SplitIndex] = p.Content;

        foreach (var frag in m)
        {
            if (frag == null || frag.Length == 0) return null;
        }

        int totalLen = 0;
        foreach (var frag in m) totalLen += frag!.Length;
        byte[] combined = new byte[totalLen];
        int offset = 0;
        foreach (var frag in m)
        {
            Array.Copy(frag!, 0, combined, offset, frag!.Length);
            offset += frag!.Length;
        }

        _splits.Remove((ushort)p.SplitId);
        p.Content = combined;
        return ReceivePacket(p);
    }

    private bool SendACK(List<uint> packets)
    {
        return SendAcknowledgement(packets, RakNetConstants.BitFlagAck, _ackBuf) == null;
    }

    private Exception? SendNACK(List<uint> packets)
    {
        return SendAcknowledgement(packets, RakNetConstants.BitFlagNack, _nackBuf);
    }

    private Exception? SendAcknowledgement(List<uint> packets, byte bitflag, MemoryStream buf)
    {
        var ack = new Acknowledgement { Packets = new List<uint>(packets) };

        while (ack.Packets.Count != 0)
        {
            buf.SetLength(0);
            buf.WriteByte((byte)(bitflag | RakNetConstants.BitFlagDatagram));
            int n = ack.Write(buf, EffectiveMTU());
            ack.Packets = ack.Packets.GetRange(n, ack.Packets.Count - n);
            var data = buf.ToArray();
            var err = WriteTo(data);
            if (err != null)
                return new Exception("send acknowledgement", err);
        }
        return null;
    }

    private void HandleACK(ReadOnlySpan<byte> b)
    {
        lock (_mu)
        {
            var ack = new Acknowledgement();
            try
            {
                ack.Read(b);
            }
            catch (Exception ex)
            {
                throw new Exception("read ACK", ex);
            }
            foreach (var seq in ack.Packets)
            {
                var (pk, ok) = _retransmission.Acknowledge(seq);
                if (ok && pk != null)
                    PacketPool.Put(pk);
            }
        }
    }

    private void HandleNACK(ReadOnlySpan<byte> b)
    {
        lock (_mu)
        {
            var nack = new Acknowledgement();
            try
            {
                nack.Read(b);
            }
            catch (Exception ex)
            {
                throw new Exception("read NACK", ex);
            }
            _ = Resend(nack.Packets);
        }
    }

    private Exception? Resend(List<uint> sequenceNumbers)
    {
        foreach (var seq in sequenceNumbers)
        {
            var (pk, ok) = _retransmission.Retransmit(seq);
            if (!ok) continue;
            var err = SendDatagram(pk!);
            if (err != null) return err;
        }
        return null;
    }

    private Exception? SendDatagram(Packet pk)
    {
        _buf.SetLength(0);
        _buf.WriteByte((byte)(RakNetConstants.BitFlagDatagram | RakNetConstants.BitFlagNeedsBAndAS));
        uint seq = _seq;
        _seq = (_seq + 1) & Binary.Uint24Mask;
        Binary.WriteUint24(_buf, seq);
        pk.Write(_buf);

        if (pk.Reliability.Reliable())
        {
            _retransmission.Add(seq, pk);
        }

        var data = _buf.ToArray();
        return WriteTo(data);
    }

    private Exception? WriteTo(byte[] data)
    {
        try
        {
            _udp.SendTo(data, SocketFlags.None, _raddr);
        }
        catch (ObjectDisposedException)
        {
            return new Exception("write to: connection closed");
        }
        catch (SocketException)
        {
            // Non-closed socket errors are logged but not returned —
            // lost packets are recovered through the resend mechanism.
        }
        return null;
    }

    /// <summary>
    /// Allocates the next dialer ID (atomic counter, always negative).
    /// </summary>
    public static long NextDialerId()
    {
        return Interlocked.Increment(ref _dialerId);
    }

    // Deadline methods — not supported on RakNet connections, matching the
    // Go implementation which returns ErrNotSupported for all three.
    public void SetReadDeadline(DateTime deadline) =>
        throw new NotSupportedException(Errors.ErrNotSupported);
    public void SetWriteDeadline(DateTime deadline) =>
        throw new NotSupportedException(Errors.ErrNotSupported);
    public void SetDeadline(DateTime deadline) =>
        throw new NotSupportedException(Errors.ErrNotSupported);
}

/// <summary>
/// Interface for message types that can serialize themselves to a byte array.
/// </summary>
public interface IMarshalable
{
    byte[] Marshal();
}
