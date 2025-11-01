
# 🚀 Faster.Transport — High-Performance Transport Framework for .NET

> **Unified Real-Time Transport Layer for .NET 6–9 Applications**  
> Fastest way to build **zero-copy**, **low-latency**, **full-duplex** communication across **TCP, UDP, IPC**, and **In-Process** backends.

`Faster.Transport` delivers a single unified abstraction — **`IParticle`** — for all transport modes:

- 🧠 **Inproc** – ultra-fast in-memory messaging inside a single process  
- 🧩 **IPC (Inter-Process Communication)** – high-speed shared-memory transport  
- ⚡ **TCP** – reliable, framed, full-duplex network transport  
- 📡 **UDP** – multicast, broadcast, and real-time datagram transport  

✅ All transports share:
- Unified **async APIs**
- **Zero-allocation send/receive**
- **Zero-copy buffer reuse**
- Consistent event-driven model

---

## 🧱 Architecture Overview

| Transport | Description | Best Use | Backing Technology |
|------------|-------------|-----------|--------------------|
| 🧠 **Inproc** | In-memory transport for subsystems within one process | Internal pipelines, game engines | Lock-free ring buffer |
| 🧩 **IPC** | Cross-process communication via shared memory | Multi-process backends, simulators | Memory-mapped files + SPSC rings |
| ⚡ **TCP** | Reliable, ordered, framed byte stream | External client/server comms | Async Sockets (length-prefixed frames) |
| 📡 **UDP** | Lightweight, low-latency datagram transport | Real-time telemetry, broadcast, multicast | Datagram sockets with multicast groups |

---

## 🧰 Core Concepts

### 🧩 `IParticle` — Unified Transport Interface

Every transport implements the same high-performance contract:

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

This guarantees plug-and-play interchangeability between `TCP`, `UDP`, `IPC`, and `Inproc` implementations.

---

## 🧪 Quick Start — Building a Transport Instance

`ParticleBuilder` provides a unified fluent API for all modes.

```csharp
var particle = new ParticleBuilder()
    .UseMode(TransportMode.Tcp)
    .ConnectTo(new IPEndPoint(IPAddress.Loopback, 9000))
    .OnConnected(p => Console.WriteLine("Connected!"))
    .OnReceived((p, data) => Console.WriteLine($"Received {data.Length} bytes"))
    .Build();
```

### Supported Transport Modes

| Enum | Transport | Description |
|------|------------|-------------|
| `TransportMode.Inproc` | In-process zero-copy | Sub-microsecond message latency |
| `TransportMode.Ipc` | Shared-memory IPC | 10× faster than named pipes |
| `TransportMode.Tcp` | Reliable socket transport | Classic client/server networking |
| `TransportMode.Udp` | Datagram transport (unicast/multicast/broadcast) | Real-time streaming or telemetry |

---

## ⚡ TCP Example

```csharp
var client = new ParticleBuilder()
    .UseMode(TransportMode.Tcp)
    .ConnectTo(new IPEndPoint(IPAddress.Loopback, 9500))
    .OnConnected(p => Console.WriteLine("TCP connected"))
    .OnReceived((p, msg) =>
        Console.WriteLine($"📩 TCP: {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

await client.SendAsync(Encoding.UTF8.GetBytes("Hello TCP!"));
```

---

## 📡 UDP Example — Full-Duplex Mode

Single socket handles both send and receive operations efficiently.

```csharp
var port = 9700;
var udp = new ParticleBuilder()
    .UseMode(TransportMode.Udp)
    .BindTo(new IPEndPoint(IPAddress.Any, port))
    .ConnectTo(new IPEndPoint(IPAddress.Loopback, port))
    .OnConnected(p => Console.WriteLine("UDP ready"))
    .OnReceived((p, msg) =>
        Console.WriteLine($"📨 {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

await udp.SendAsync(Encoding.UTF8.GetBytes("Ping via UDP!"));
```

---

## 🌍 UDP Multicast Example

Broadcast to all peers in a multicast group — perfect for telemetry or discovery.

```csharp
var group = IPAddress.Parse("239.0.0.123");
var port = 9700;

var peer = new ParticleBuilder()
    .UseMode(TransportMode.Udp)
    .EnableMulticast(group, port, disableLoopback: false)
    .OnConnected(p => Console.WriteLine("✅ Joined multicast group"))
    .OnReceived((p, msg) =>
        Console.WriteLine($"📩 {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

await peer.SendAsync(Encoding.UTF8.GetBytes("Hello multicast group!"));
```

💡 **Tip:** Set `disableLoopback: true` to avoid receiving your own packets.

---

## 🧠 In-Process (Inproc) Example

Super-fast in-memory message passing (no kernel overhead).

```csharp
// Server
var server = new ParticleBuilder()
    .UseMode(TransportMode.Inproc)
    .WithChannel("demo", isServer: true)
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"[Server] {Encoding.UTF8.GetString(msg.Span)}");
        p.Send("Echo"u8.ToArray());
    })
    .Build();

// Client
var client = new ParticleBuilder()
    .UseMode(TransportMode.Inproc)
    .WithChannel("demo")
    .OnReceived((p, msg) =>
        Console.WriteLine($"[Client] Reply: {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

await client.SendAsync("Ping"u8.ToArray());
```

---

## 🧩 IPC Example — Cross-Process Messaging

High-speed interprocess communication using memory-mapped files and SPSC rings.

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
        Console.WriteLine($"[Client] Got: {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

await client.SendAsync("Hi IPC!"u8.ToArray());
```

---

## ⚙️ Benchmark Results (.NET 9, x64, Release)

| Transport | Scenario | Messages | Mean | Allocated | Notes |
|------------|-----------|----------|------|------------|-------|
| 🧠 **Inproc** | 10k async messages | 10 000 | ** 0.8 ms 🏆** | 956 KB | Lock-free ring buffer |
| 🧩 **IPC** | 10k async messages | 10 000 | 1.803 ms | 184 B  | Shared memory (MMF) |
| 📡 **TCP** | 10k async messages| 10 000 | 76.82 ms | 1.3 MB | saea |
| 📡 **UDP** | 10k datagrams | 10 000 | 92.82 ms | 1.6 MB | unicast |
| 📡 **UDP** | 10k datagrams | 10 000 | 502.20 ms | 1.6 MB | multicast |

All benchmarks performed using **BenchmarkDotNet** on **.NET 9.0**  
CPU: AMD Ryzen 9 5950X | 64 GB DDR4 | Windows 11 x64  

---

## ⚙️ Common Builder Options

| Method | Description |
|--------|-------------|
| `.UseMode(TransportMode)` | Select transport backend |
| `.BindTo(EndPoint)` | Sets local socket endpoint |
| `.ConnectTo(EndPoint)` | Defines remote target |
| `.EnableMulticast(IPAddress, port, disableLoopback)` | Joins a multicast group |
| `.AllowBroadcast(bool)` | Enables UDP broadcast mode |
| `.WithChannel(string, bool)` | Sets shared channel name for IPC/Inproc |
| `.WithBufferSize(int)` | Sets internal buffer size |
| `.WithParallelism(int)` | Controls parallel async sends |
| `.OnReceived(Action<IParticle, ReadOnlyMemory<byte>>)` | Handles incoming data |
| `.OnConnected(Action<IParticle>)` | Invoked when ready |
| `.OnDisconnected(Action<IParticle>)` | Invoked when closed |

---

## 🔍 Keywords for Developers & SEO

**Tags:**  
`.NET transport layer`, `.NET networking`, `zero-copy IPC`, `shared memory communication`,  
`low latency TCP`, `UDP multicast broadcast`, `async sockets`,  
`real-time telemetry`, `message bus`, `lock-free ring buffer`, `C# networking library`.

**Use Cases:**  
Real-time trading systems · Game networking · Simulation · Distributed telemetry · Robotics · HFT

---

## 🧾 License

MIT © 2025 — **Faster.Transport** Team  
Optimized for **real-time**, **low-latency**, **high-throughput** distributed systems.
