# ⚡ Faster.Transport  
**Ultra-low-latency, high-throughput transport layer for real-time distributed systems**

Faster.Transport is a modern, zero-allocation, high-performance networking library designed for **real-time data transport**.  
It provides an event-driven **TCP Reactor** (server) and multiple specialized **Particles** (clients) for different concurrency and throughput models — optimized for **trading engines**, **telemetry**, **simulation**, and **multiplayer networking**.

---

## 🚀 Core Components

| Component | Description | Protocol | Ideal Use Case |
|------------|-------------|-----------|----------------|
| 🧠 **Reactor** | High-performance async TCP server using `SocketAsyncEventArgs` and zero-copy I/O. Manages multiple clients efficiently. | TCP | Low-latency message hubs, servers, brokers |
| ⚙️ **Particle** | Single-threaded async client with `await`-based send/receive. | TCP | Reliable request/response, command streaming |
| 🌐 **ParticleFlux** | Multi-threaded concurrent client (safe for many producers). Uses lock-free buffer pools. | TCP | Parallel telemetry uploads, multi-threaded simulations |
| ⚡ **ParticleBurst** | Fire-and-forget ultra-fast client. Trades reliability for raw throughput. | TCP | Tick feeds, sensor data, broadcast updates |

---

## 🧩 Architecture Overview

```
 ┌────────────────────────────┐
 │        Reactor (Server)    │
 │  ────────────────────────  │
 │  Accepts clients as        │
 │  Connection objects         │
 │  Handles framed messages    │
 └─────────────┬──────────────┘
               │
      ┌────────┴────────┐
      │                 │
┌────────────┐   ┌────────────┐
│  Particle   │   │ ParticleFlux │
│ (Async)     │   │ (Concurrent) │
└────────────┘   └────────────┘
       │                 │
       └─────────┬───────┘
                 │
          ┌────────────┐
          │ ParticleBurst │
          │ (Fire & Forget) │
          └────────────┘
```

Each **Particle** connects to a **Reactor** and communicates using a lightweight **framed protocol**:

```
[length:int32][payload:byte[]]
```

This enables efficient, zero-copy parsing of variable-length messages.

---

## 🧠 What Are Particles?

Particles are **clients that connect to a Reactor**.  
Each type offers a specific balance between **throughput**, **latency**, and **concurrency safety**.

| Particle Type | Description | Thread Safety | Reliability | Throughput | Typical Use |
|----------------|--------------|----------------|---------------|--------------|---------------|
| **Particle** | Async client (single-threaded) using `ValueTask SendAsync`. | 🚫 No | ✅ Reliable | ⚙️ Moderate | RPCs, control messages |
| **ParticleFlux** | Concurrent async client (multi-threaded safe). | ✅ Yes | ✅ Reliable | 🚀 High | Parallel telemetry streams |
| **ParticleBurst** | Fire-and-forget, lock-free burst sender. | ✅ Yes | ⚠️ Unreliable (no await) | ⚡ Extreme | Market data, tick streams, sensor bursts |

---

## 🧩 Example — Reactor + Particle

### Reactor (Server)

```csharp
using Faster.Transport;
using System.Net;

var reactor = new Reactor(new IPEndPoint(IPAddress.Any, 5555));
reactor.OnReceived = (conn, data) =>
{
    // Echo message back
    conn.Return(data);
};

reactor.OnConnected = conn => Console.WriteLine("New client connected!");
reactor.Start();

Console.WriteLine("Reactor started on port 5555.");
Console.ReadLine();
```

### Particle (Async Client)

```csharp
using Faster.Transport;
using System.Net;
using System.Text;

var particle = new ParticleBuilder()
    .ConnectTo(new IPEndPoint(IPAddress.Loopback, 5555))
    .OnReceived(data => Console.WriteLine("Echo: " + Encoding.UTF8.GetString(data.Span)))
    .OnParticleDisconnected((cli, ex) => Console.WriteLine("Disconnected: " + ex?.Message))
    .Build();

await particle.SendAsync(Encoding.UTF8.GetBytes("Hello Reactor!"));
```

---

## ⚡ Example — ParticleBurst (Fire-and-Forget)

```csharp
using Faster.Transport;
using System.Net;
using System.Text;

var burst = new ParticleBuilder()
    .ConnectTo(new IPEndPoint(IPAddress.Loopback, 5555))
    .AsBurst()
    .WithParallelism(32)
    .OnBurstDisconnected((cli, ex) => Console.WriteLine("Burst disconnected: " + ex?.Message))
    .BuildBurst();

var payload = Encoding.UTF8.GetBytes("Hello Reactor ⚡");

// Send 100k messages as fast as possible
for (int i = 0; i < 100_000; i++)
{
    burst.Send(payload);
}
```

---

## 🧰 Features

✅ Zero-copy, framed protocol  
✅ Lock-free buffer management (`ConcurrentBufferManager`)  
✅ Pooled `SocketAsyncEventArgs` for zero allocation  
✅ High-performance frame parser with inline feed  
✅ Full duplex I/O  
✅ Supports hundreds of concurrent connections  
✅ Works with .NET Framework 4.8 and .NET 6+  

---

## ⚙️ Configuration via `ParticleBuilder`

| Method | Description |
|--------|--------------|
| `.ConnectTo(EndPoint)` | Sets the remote endpoint |
| `.WithBufferSize(int)` | Controls per-message buffer slice |
| `.WithParallelism(int)` | Controls internal pool scaling |
| `.AsConcurrent()` | Enables thread-safe concurrent sends |
| `.AsBurst()` | Enables fire-and-forget mode |
| `.OnReceived(Action<ReadOnlyMemory<byte>>)` | Handles received frames |
| `.OnParticleDisconnected(...)` | Handles disconnect for async particles |
| `.OnBurstDisconnected(...)` | Handles disconnect for burst particles |

---

## 📦 Example Project Scenarios

| Scenario | Recommended |
|-----------|--------------|
| Command/Control API | 🧩 `Particle` |
| Multi-threaded telemetry upload | 🌐 `ParticleFlux` |
| Firehose of tick or sensor data | ⚡ `ParticleBurst` |
| Server or message router | 🧠 `Reactor` |

---

## 🧪 Performance Targets (on modern hardware)

| Metric | ParticleFlux | ParticleBurst |
|---------|---------------|----------------|
| Throughput | ~3–5 million msgs/sec | ~10+ million msgs/sec |
| Latency | ~40 µs (99%) | ~25 µs (99%) |
| Allocations | Zero | Zero |

*(Tested with 8192-byte payloads on loopback with 32 parallel senders.)*

---

## 🧩 License

MIT © Faster.Transport  
Engineered for **speed**, **stability**, and **real-time data flow**.

