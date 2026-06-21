using System;

namespace RakNet.Messages;

/// <summary>
/// RakNet packet IDs used throughout the protocol.
/// </summary>
public static class Id
{
    public const byte ConnectedPing = 0x00;
    public const byte UnconnectedPing = 0x01;
    public const byte UnconnectedPingOpenConnections = 0x02;
    public const byte ConnectedPong = 0x03;
    public const byte DetectLostConnections = 0x04;
    public const byte OpenConnectionRequest1 = 0x05;
    public const byte OpenConnectionReply1 = 0x06;
    public const byte OpenConnectionRequest2 = 0x07;
    public const byte OpenConnectionReply2 = 0x08;
    public const byte ConnectionRequest = 0x09;
    public const byte ConnectionRequestAccepted = 0x10;
    public const byte NewIncomingConnection = 0x13;
    public const byte DisconnectNotification = 0x15;
    public const byte IncompatibleProtocolVersion = 0x19;
    public const byte UnconnectedPong = 0x1c;
}

/// <summary>
/// The 16-byte magic sequence found in every unconnected RakNet message.
/// </summary>
public static class Magic
{
    public static readonly byte[] Sequence =
    {
        0x00, 0xff, 0xff, 0x00, 0xfe, 0xfe, 0xfe, 0xfe,
        0xfd, 0xfd, 0xfd, 0xfd, 0x12, 0x34, 0x56, 0x78
    };
}
