# RakNet

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](RakNet.csproj)

> [English](README.md)

RakNet 网络协议库的纯 C# 实现，专为游戏和实时应用程序设计。此库提供基于 UDP 的可靠消息传输，支持多种可靠性级别、自动重传、MTU 发现和 DoS 防护。

此项目是 [RakNet](https://github.com/facebookarchive/RakNet) 协议的 C# 移植

---

## 安装

### 要求

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 或更高版本

### 添加到项目

```bash
dotnet add reference path/to/RakNet.csproj
```

或者直接引用项目文件：

```xml
<ItemGroup>
  <ProjectReference Include="path/to/RakNet.csproj" />
</ItemGroup>
```

---

## 快速开始

### 启动服务端

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

### 连接客户端

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

### Unconnected Ping（离线探测）

```csharp
var dialer = new Dialer();
byte[] pongData = dialer.Ping("127.0.0.1:19132");
Console.WriteLine($"Server replied with {pongData.Length} bytes of pong data");
```

---

## API 概述

### 核心类型

| 类型 | 说明 |
|------|------|
| `Listener` | 服务端监听器，绑定 UDP 端口并接受连接 |
| `Dialer` | 客户端拨号器，连接远程服务端 |
| `Conn` | 连接实例，提供 `Read()` / `Write()` 读写接口 |
| `Packet` | 协议层数据包封装，包含可靠性元数据和分片信息 |
| `Reliability` | 可靠性级别枚举 |
| `IConnectionHandler` | 连接处理器接口，处理协议内部包 |

### 可靠性级别

| 级别 | 值 | 说明 |
|------|-----|------|
| `Unreliable` | 0 | 不可靠、无序，最快但可能丢包 |
| `UnreliableSequenced` | 1 | 不可靠但有序，丢弃旧包 |
| `Reliable` | 2 | 可靠但不保证顺序 |
| `ReliableOrdered` | 3 | 可靠且保序（默认） |
| `ReliableSequenced` | 4 | 可靠但只保留最新 |

### 配置选项

```csharp
public class Dialer
{
    public Action<string>? ErrorLog;        // 错误日志回调
    public int MaxTransientErrors;          // 最大临时错误次数
    public ushort MaxMTU;                   // 最大 MTU
    public IUpstreamDialer? UpstreamDialer; // 自定义 UDP 套接字创建
}

public class ListenConfig
{
    public Action<string>? ErrorLog;        // 错误日志回调
    public bool DisableCookies;             // 禁用 Cookie（不推荐）
    public ushort MaxMTU;                   // 最大 MTU
    public TimeSpan BlockDuration;          // IP 封禁时长（默认 10 秒）
}
```

---

## 许可证

此项目基于 [MIT 许可证](LICENSE) 开源。

版权所有 (c) 2026 MCBE Community
