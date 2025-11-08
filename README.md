# 🚀 Faster.Transport — High-Performance Transport Framework for .NET

> **Unified Real-Time Transport Layer for .NET 6–9 Applications**  
> The fastest way to build **zero-copy**, **low-latency**, **full-duplex** communication across **TCP, UDP, IPC**, and **In-Process** backends.

`Faster.Transport` delivers a unified abstraction — **`IParticle`** — that powers all modes:

- 🧠 **Inproc** – ultra-fast in-memory messaging within one process  
- 🧩 **IPC** – shared-memory communication across processes  
- ⚡ **TCP** – reliable, framed, full-duplex network transport  
- 📡 **UDP** – lightweight, multicast-capable datagram transport  

✅ All transport modes share:
- Unified **async + sync APIs**
- **Zero-allocation send/receive**
- **Zero-copy buffer reuse**
- **Consistent event-driven model**

---

## 🧱 Architecture Overview

| Transport | Description | Best Use | Backing Technology |
|------------|-------------|-----------|--------------------|
| 🧠 **Inproc** | In-memory transport within a single process | Internal pipelines, subsystems | Lock-free MPSC queue |
| 🧩 **IPC** | Cross-process shared-memory transport | Multi-process backends | Memory-mapped files + SPSC rings |
| ⚡ **TCP** | Reliable, ordered, framed byte stream | External services, LAN/WAN | Async Sockets (length-prefixed frames) |
| 📡 **UDP** | Lightweight, low-latency datagrams | Telemetry, discovery, broadcast | Datagram sockets with multicast |

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

All modes (TCP, UDP, IPC, Inproc) share this exact API and semantics.

---

## ⚙️ Builders Overview

There are **two primary builders**:

| Builder | Role | Description |
|----------|------|-------------|
| 🧱 `ReactorBuilder` | Server | Creates a multi-client server “reactor” that spawns `IParticle` peers automatically. |
| ⚙️ `ParticleBuilder` | Client | Creates a single transport client for any mode (TCP, UDP, IPC, Inproc). |

Both share the same fluent configuration API.

---

## ⚡ TCP Examples

### 🧱 TCP Server (Reactor)

```csharp
var server = new ReactorBuilder()
    .UseMode(TransportMode.Tcp)
    .WithLocal(new IPEndPoint(IPAddress.Any, 9500))
    .OnConnected(p => Console.WriteLine("🟢 Client connected"))
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"Server received: {Encoding.UTF8.GetString(msg.Span)}");
        p.Send("Echo"u8.ToArray());
    })
    .Build();

server.Start();
Console.WriteLine("✅ TCP server running on port 9500");
```

### ⚙️ TCP Client

```csharp
var client = new ParticleBuilder()
    .UseMode(TransportMode.Tcp)
    .WithRemote(new IPEndPoint(IPAddress.Loopback, 9500))
    .OnConnected(p => Console.WriteLine("✅ Connected to TCP server"))
    .OnReceived((p, msg) =>
        Console.WriteLine($"📩 {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

await client.SendAsync("Hello TCP!"u8.ToArray());
```

💡 `ReactorBuilder` manages client lifetimes; each new connection spawns a dedicated `IParticle`.

---

## 📡 UDP Example (Full Duplex)

```csharp
var port = 9700;

var udp = new ParticleBuilder()
    .UseMode(TransportMode.Udp)
    .WithLocal(new IPEndPoint(IPAddress.Any, port))
    .WithRemote(new IPEndPoint(IPAddress.Loopback, port))
    .AllowBroadcast(true)
    .OnConnected(p => Console.WriteLine("📡 UDP ready"))
    .OnReceived((p, msg) =>
        Console.WriteLine($"📨 {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

await udp.SendAsync("Ping via UDP!"u8.ToArray());
```

---

## 🌍 UDP Multicast Example

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

await peer.SendAsync("Hello multicast group!"u8.ToArray());
```

💡 Use `disableLoopback: true` to suppress receiving your own datagrams.

---

## 🧠 In-Process (Inproc) Example

Ultra-low-latency in-memory communication within the same process (no syscalls).

```csharp
// Server
var server = new ReactorBuilder()
    .UseMode(TransportMode.Inproc)
    .WithChannel("demo")
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
        Console.WriteLine($"[Client] {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

server.Start();
await client.SendAsync("Ping"u8.ToArray());
```

💡 Inproc mode uses a **lock-free MPSC queue** with hybrid event-driven polling.

---

## 🧩 IPC Example — Cross-Process Messaging

High-performance interprocess communication using **shared memory** and **ring buffers**.

```csharp
// Server
var server = new ReactorBuilder()
    .UseMode(TransportMode.Ipc)
    .WithChannel("shared-mem")
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

server.Start();
await client.SendAsync("Hi IPC!"u8.ToArray());
```

💡 IPC mode uses **shared memory + SPSC ring buffers** and supports multiple clients per reactor.

---

## ⚙️ Common Builder Options

| Method | Description |
|--------|-------------|
| `.UseMode(TransportMode)` | Select transport backend (TCP, UDP, IPC, Inproc) |
| `.AsServer(bool)` | Explicitly mark as server (alternative to `ReactorBuilder`) |
| `.WithLocal(IPEndPoint)` | Bind address for TCP/UDP |
| `.WithRemote(IPEndPoint)` | Remote endpoint for TCP/UDP |
| `.WithMulticast(IPAddress, int, bool)` | Join a UDP multicast group |
| `.AllowBroadcast(bool)` | Enable UDP broadcast |
| `.WithChannel(string, bool)` | Channel name for IPC/Inproc |
| `.WithBufferSize(int)` | Per-connection buffer size |
| `.WithParallelism(int)` | Control async send parallelism |
| `.WithTcpBacklog(int)` | TCP backlog size |
| `.WithAutoReconnect(double, double)` | Automatic reconnect (minDelay, maxDelay in seconds) |
| `.OnReceived(Action<IParticle, ReadOnlyMemory<byte>>)` | Message handler |
| `.OnConnected(Action<IParticle>)` | Connection established |
| `.OnDisconnected(Action<IParticle>)` | Connection closed |

---

## 🧪 Benchmark Results (.NET 9, x64, Release)

| Transport | Scenario | Messages | Mean | Allocated | Notes |
|------------|-----------|----------|------|------------|-------|
| 🧠 **Inproc** | 10k async messages | 10 000 | **0.8 ms 🏆** | 956 KB | Lock-free MPSC queue |
| 🧩 **IPC** | 10k async messages | 10 000 | 1.8 ms | 184 B | Shared memory (MMF) |
| ⚡ **TCP** | 10k async messages | 10 000 | 76.8 ms | 1.3 MB | SAEA framed protocol |
| 📡 **UDP (Unicast)** | 10k datagrams | 10 000 | 92.8 ms | 1.6 MB | Datagram sockets |
| 📡 **UDP (Multicast)** | 10k datagrams | 10 000 | 502.2 ms | 1.6 MB | Multicast group |

Tested with **BenchmarkDotNet** on **.NET 9.0**,  
CPU: AMD Ryzen 9 5950X | 64 GB DDR4 | Windows 11 x64

---

## 🔍 Keywords for Developers

**Tags:**  
`.NET transport layer`, `zero-copy IPC`, `shared memory`,  
`low latency TCP`, `UDP multicast`, `async sockets`,  
`real-time telemetry`, `lock-free ring buffer`, `C# networking`

**Use Cases:**  
Real-time trading · Game networking · Simulation · Distributed telemetry · Robotics · HFT systems

---

## 🧾 License

MIT © 2025 — **Faster.Transport** Team  
Optimized for **real-time**, **low-latency**, and **high-throughput** distributed systems.
