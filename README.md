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

## 🧩 `IParticle` — Unified Transport Interface

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

All modes — TCP, UDP, IPC, and Inproc — share this exact API.

---

## ⚙️ Building Transports with `ParticleBuilder`

`ParticleBuilder` provides a unified fluent API to construct any transport — client or server.

```csharp
var particle = new ParticleBuilder()
    .UseMode(TransportMode.Tcp)
    .WithRemote(new IPEndPoint(IPAddress.Loopback, 9000))
    .OnConnected(p => Console.WriteLine("Connected!"))
    .OnReceived((p, data) => Console.WriteLine($"Received {data.Length} bytes"))
    .Build();
```

---

## ⚡ TCP Examples

### 🧠 TCP Client

```csharp
var client = new ParticleBuilder()
    .UseMode(TransportMode.Tcp)
    .WithRemote(new IPEndPoint(IPAddress.Loopback, 9500))
    .OnConnected(p => Console.WriteLine("✅ TCP connected"))
    .OnReceived((p, msg) =>
        Console.WriteLine($"📩 TCP: {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

await client.SendAsync(Encoding.UTF8.GetBytes("Hello TCP!"));
```

### 🧱 TCP Server (Reactor Mode)

```csharp
var server = new ParticleBuilder()
    .UseMode(TransportMode.Tcp)
    .AsServer(true)
    .WithLocal(new IPEndPoint(IPAddress.Any, 9500))
    .OnConnected(p => Console.WriteLine("🟢 Client connected"))
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"Server got: {Encoding.UTF8.GetString(msg.Span)}");
        p.Send("Echo"u8.ToArray());
    })
    .Build();
```

💡 The TCP server uses the **high-performance `Reactor`** architecture —  
zero allocations, async accept loop, and automatic per-client `Particle` management.

---

## 📡 UDP Example — Full-Duplex Mode

Single socket handles both send and receive operations efficiently.

```csharp
var port = 9700;

var udp = new ParticleBuilder()
    .UseMode(TransportMode.Udp)
    .WithLocal(new IPEndPoint(IPAddress.Any, port))
    .WithRemote(new IPEndPoint(IPAddress.Loopback, port))
    .AllowBroadcast(true)
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
    .WithMulticast(group, port, disableLoopback: false)
    .OnConnected(p => Console.WriteLine("✅ Joined multicast group"))
    .OnReceived((p, msg) =>
        Console.WriteLine($"📩 {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

await peer.SendAsync(Encoding.UTF8.GetBytes("Hello multicast group!"));
```

💡 Use `disableLoopback: true` to prevent receiving your own packets.

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

High-speed interprocess communication using shared memory and SPSC rings.

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

## ⚙️ Common Builder Options

| Method | Description |
|--------|-------------|
| `.UseMode(TransportMode)` | Selects transport backend |
| `.AsServer(bool)` | Enables server mode (TCP, IPC, or Inproc) |
| `.WithLocal(IPEndPoint)` | Sets the local bind address |
| `.WithRemote(IPEndPoint)` | Sets the remote endpoint |
| `.WithMulticast(IPAddress, int, bool)` | Joins a UDP multicast group |
| `.AllowBroadcast(bool)` | Enables UDP broadcast |
| `.WithChannel(string, bool)` | Sets the channel name (IPC/Inproc) |
| `.WithBufferSize(int)` | Configures per-connection buffer size |
| `.WithParallelism(int)` | Controls async send parallelism |
| `.WithTcpBacklog(int)` | Sets TCP server backlog size |
| `.WithAutoReconnect(double, double)` | Enables exponential reconnect retry |
| `.OnReceived(Action<IParticle, ReadOnlyMemory<byte>>)` | Handler for incoming data |
| `.OnConnected(Action<IParticle>)` | Invoked when ready or connected |
| `.OnDisconnected(Action<IParticle>)` | Invoked when closed/disconnected |

---

## 🧪 Benchmark Results (.NET 9, x64, Release)

| Transport | Scenario | Messages | Mean | Allocated | Notes |
|------------|-----------|----------|------|------------|-------|
| 🧠 **Inproc** | 10k async messages | 10 000 | **0.8 ms 🏆** | 956 KB | Lock-free ring buffer |
| 🧩 **IPC** | 10k async messages | 10 000 | 1.8 ms | 184 B | Shared memory (MMF) |
| ⚡ **TCP** | 10k async messages | 10 000 | 76.8 ms | 1.3 MB | SAEA framed protocol |
| 📡 **UDP (Unicast)** | 10k datagrams | 10 000 | 92.8 ms | 1.6 MB | Datagram sockets |
| 📡 **UDP (Multicast)** | 10k datagrams | 10 000 | 502.2 ms | 1.6 MB | Multicast group |

All benchmarks executed using **BenchmarkDotNet** on **.NET 9.0**  
CPU: AMD Ryzen 9 5950X | 64 GB DDR4 | Windows 11 x64

---

## 🔍 Keywords for Developers

**Tags:**  
`.NET transport layer`, `.NET networking`, `zero-copy IPC`, `shared memory communication`,  
`low latency TCP`, `UDP multicast broadcast`, `async sockets`,  
`real-time telemetry`, `message bus`, `lock-free ring buffer`, `C# networking library`

**Use Cases:**  
Real-time trading · Game networking · Simulation · Distributed telemetry · Robotics · HFT systems

---

## 🧾 License

MIT © 2025 — **Faster.Transport** Team  
Optimized for **real-time**, **low-latency**, **high-throughput** distributed systems.
