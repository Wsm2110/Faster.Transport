# 🚀 Faster.Transport

> **Unified High-Performance Transport Layer for .NET**

`Faster.Transport` provides a **single abstraction (`IParticle`)** for multiple communication backends:
- 🧠 **Inproc** – ultra-fast in-memory messaging inside one process  
- 🧩 **IPC** – shared-memory interprocess communication  
- ⚡ **TCP** – reliable framed network transport  
- 📡 **UDP** – multicast, broadcast, and low-latency datagrams  

Each transport implements **full-duplex communication**, unified **async APIs**, and supports **zero-copy buffer reuse** for extreme speed.

---

## 🧱 Architecture Overview

| Transport | Description | Best For | Backing Technology |
|------------|-------------|----------|--------------------|
| 🧠 **Inproc** | Runs entirely in memory within one process | Same-process subsystems | Lock-free ring buffer |
| 🧩 **IPC** | High-speed shared-memory communication between processes | Cross-process communication | Memory-mapped files + SPSC rings |
| ⚡ **TCP** | Reliable, ordered, framed byte stream | External connections | Sockets with length-prefixed framing |
| 📡 **UDP** | Lightweight datagram messaging with multicast/broadcast support | Real-time telemetry | Datagram sockets with optional multicast groups |

---

## 🧰 Core Concepts

### 🧩 `IParticle`

Every transport implements the same core interface:

```csharp
public interface IParticle : IDisposable
{
    Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }
    Action<IParticle>? OnDisconnected { get; set; }
    Action<IParticle>? OnConnected { get; set; }

    ValueTask SendAsync(ReadOnlyMemory<byte> payload);
    void Send(ReadOnlySpan<byte> payload);
}
```

This ensures that any `IParticle` instance — TCP, UDP, IPC, or Inproc — can be used interchangeably in your systems.

---

## 🧪 Building a Transport Instance

All transports are created via the unified **`ParticleBuilder`**.

```csharp
var particle = new ParticleBuilder()
    .UseMode(TransportMode.Tcp)
    .ConnectTo(new IPEndPoint(IPAddress.Loopback, 9000))
    .OnConnected(p => Console.WriteLine("Connected!"))
    .OnReceived((p, data) => Console.WriteLine($"Received {data.Length} bytes"))
    .Build();
```

### Supported Modes

| Enum | Transport | Description |
|------|------------|-------------|
| `TransportMode.Inproc` | In-process zero-copy | Super low latency internal messaging |
| `TransportMode.Ipc` | Shared-memory cross-process | 10x faster than named pipes |
| `TransportMode.Tcp` | Framed, reliable socket transport | Traditional client/server |
| `TransportMode.Udp` | Datagram transport (unicast/multicast/broadcast) | Real-time telemetry |

---

## ⚡ TCP Example

```csharp
var client = new ParticleBuilder()
    .UseMode(TransportMode.Tcp)
    .ConnectTo(new IPEndPoint(IPAddress.Loopback, 9500))
    .OnConnected(p => Console.WriteLine("TCP connected"))
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"📩 TCP received: {Encoding.UTF8.GetString(msg.Span)}");
    })
    .Build();

await client.SendAsync(Encoding.UTF8.GetBytes("Hello TCP!"));
```

---

## 📡 UDP Example (Full Duplex)

Full-duplex UDP socket for **send + receive** on one port.

```csharp
var port = 9700;
var local = new IPEndPoint(IPAddress.Any, port);
var remote = new IPEndPoint(IPAddress.Loopback, port);

var udp = new ParticleBuilder()
    .UseMode(TransportMode.Udp)
    .BindTo(local)
    .ConnectTo(remote)
    .OnConnected(p => Console.WriteLine("UDP ready"))
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"📨 {Encoding.UTF8.GetString(msg.Span)}");
    })
    .Build();

await udp.SendAsync(Encoding.UTF8.GetBytes("Ping via UDP!"));
```

---

## 🌍 UDP Multicast Example

Broadcast messages to all peers in a multicast group.

```csharp
var group = IPAddress.Parse("239.0.0.123");
var port = 9700;
var local = new IPEndPoint(IPAddress.Any, 0);
var multicast = new IPEndPoint(group, port);

var peer = new ParticleBuilder()
    .UseMode(TransportMode.Udp)
    .BindTo(local)
    .ConnectTo(multicast)
    .JoinMulticastGroup(group, disableLoopback: false)
    .OnConnected(p => Console.WriteLine("✅ Joined multicast group"))
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"📩 {Encoding.UTF8.GetString(msg.Span)}");
    })
    .Build();

await peer.SendAsync(Encoding.UTF8.GetBytes("Hello multicast group!"));
```

🧠 **Tip:**  
Set `disableLoopback: true` if you don’t want to receive your own packets.

---

## 🧠 Inproc Example

Ultra-fast messaging inside a single process (no kernel calls):

```csharp
// Create server side
var server = new ParticleBuilder()
    .UseMode(TransportMode.Inproc)
    .WithChannel("demo", isServer: true)
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"[Server] Got: {Encoding.UTF8.GetString(msg.Span)}");
        p.Send("Echo"u8.ToArray());
    })
    .Build();

// Create client side
var client = new ParticleBuilder()
    .UseMode(TransportMode.Inproc)
    .WithChannel("demo")
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"[Client] Got reply: {Encoding.UTF8.GetString(msg.Span)}");
    })
    .Build();

await client.SendAsync("Ping"u8.ToArray());
```

---

## 🧩 IPC Example (Cross-Process)

Uses **memory-mapped files** and ring buffers to exchange messages across processes.

```csharp
// Server
var server = new ParticleBuilder()
    .UseMode(TransportMode.Ipc)
    .WithChannel("shared-mem", isServer: true)
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"[Server] {Encoding.UTF8.GetString(msg.Span)}");
        p.Send("Ack"u8.ToArray());
    })
    .Build();

// Client
var client = new ParticleBuilder()
    .UseMode(TransportMode.Ipc)
    .WithChannel("shared-mem")
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"[Client] Got: {Encoding.UTF8.GetString(msg.Span)}");
    })
    .Build();

await client.SendAsync("Hi IPC!"u8.ToArray());
```

---

## ⚙️ Common Builder Options

| Method | Description |
|--------|--------------|
| `.UseMode(TransportMode)` | Selects backend |
| `.BindTo(EndPoint)` | Sets local endpoint (UDP/TCP) |
| `.ConnectTo(EndPoint)` | Sets remote target |
| `.JoinMulticastGroup(IPAddress, disableLoopback)` | Enables multicast for UDP |
| `.WithChannel(string, bool)` | Sets shared channel for IPC/Inproc |
| `.AllowBroadcast(bool)` | Enables UDP broadcast |
| `.WithBufferSize(int)` | Adjusts internal buffer size |
| `.WithParallelism(int)` | Controls async send parallelism |
| `.OnReceived(Action<IParticle, ReadOnlyMemory<byte>>)` | Handles received messages |
| `.OnConnected(Action<IParticle>)` | Called when ready |
| `.OnDisconnected(Action<IParticle, Exception?>)` | Handles disconnects |

---

## 🧾 License

MIT © 2025 — Faster.Transport Team  
Designed for high-performance real-time systems, simulation, and distributed computation.
