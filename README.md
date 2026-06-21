# RakNet

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](RakNet.csproj)

> [Chinese](readme_zh.md)

A pure C# implementation of the RakNet networking protocol for games and real-time applications. This library provides reliable message transport over UDP with multiple reliability levels, automatic retransmission, MTU discovery, and DoS protection.

This is a C# port of the original [RakNet](https://github.com/facebookarchive/RakNet) protocol.
---

## Installation

### Requirements

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later

### Add to your project

```bash
dotnet add reference path/to/RakNet.csproj
```

Or add a project reference:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/RakNet.csproj" />
</ItemGroup>
```

---

## Quick Start

### Start a server

```csharp
using RakNet;

var listener = new ListenConfig
{
    ErrorLog = msg => Console.WriteLine($"Error: {msg}")
}.Listen("0.0.0.0:19132");

Console.WriteLine($"Server listening on {listener.LocalEndPoint}");

while (true)
{
    var conn = listener.Accept();
    Console.WriteLine($"New connection from {conn.RemoteEndPoint}");

    new Thread(() =>
    {
        try
        {
            while (true)
            {
                byte[] data = conn.ReadPacket();
                conn.Write(data); // Echo back
            }
        }
        catch { }
    }) { IsBackground = true }.Start();
}
```

### Connect as a client

```csharp
using RakNet;

var dialer = new Dialer
{
    ErrorLog = msg => Console.WriteLine($"Error: {msg}")
};

var conn = dialer.Dial("127.0.0.1:19132");
Console.WriteLine($"Connected to {conn.RemoteEndPoint}");

conn.Write(System.Text.Encoding.UTF8.GetBytes("Hello, RakNet!"));

byte[] response = conn.ReadPacket();
Console.WriteLine($"Received: {System.Text.Encoding.UTF8.GetString(response)}");

conn.Close();
```

### Unconnected ping (server discovery)

```csharp
var dialer = new Dialer();
byte[] pongData = dialer.Ping("127.0.0.1:19132");
Console.WriteLine($"Server replied with {pongData.Length} bytes of pong data");
```

---

## API Overview

### Core Types

| Type | Description |
|------|-------------|
| `Listener` | Server listener — binds a UDP port and accepts incoming connections |
| `Dialer` | Client dialer — connects to a remote server |
| `Conn` | Connection instance — provides `Read()` / `Write()` for data exchange |
| `Packet` | Protocol-level packet encapsulation with reliability metadata and split info |
| `Reliability` | Enum of reliability levels |
| `IConnectionHandler` | Interface for handling internal RakNet packets |

### Reliability Levels

| Level | Value | Description |
|-------|-------|-------------|
| `Unreliable` | 0 | No reliability, no ordering — fastest but may drop packets |
| `UnreliableSequenced` | 1 | Unreliable but sequenced — older packets are discarded |
| `Reliable` | 2 | Reliable but no ordering guarantee |
| `ReliableOrdered` | 3 | Reliable and ordered (default) |
| `ReliableSequenced` | 4 | Reliable but only the latest is kept |

### Configuration

```csharp
public class Dialer
{
    public Action<string>? ErrorLog;        // Error log callback
    public int MaxTransientErrors;          // Maximum transient errors before abort
    public ushort MaxMTU;                   // Maximum MTU
    public IUpstreamDialer? UpstreamDialer; // Custom UDP socket factory
}

public class ListenConfig
{
    public Action<string>? ErrorLog;        // Error log callback
    public bool DisableCookies;             // Disable anti-spoofing cookies (not recommended)
    public ushort MaxMTU;                   // Maximum MTU
    public TimeSpan BlockDuration;          // IP block duration (default 10s)
}
```

---

## License

This project is licensed under the [MIT License](LICENSE).

Copyright (c) 2026 MCBE Community
