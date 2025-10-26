# 🚀 Faster.Transport

**Faster.Transport** is a high-performance, unified transport layer for .NET that abstracts multiple communication backends — including **In-Process**, **Shared Memory (IPC)**, **TCP**, and **UDP / Multicast** — under a single `IParticle` interface.

It is designed for **ultra-low-latency**, **high-throughput** messaging across local, inter-process, and network boundaries.

---

## ✨ Features

| Feature | Description |
|----------|-------------|
| 🧠 **Unified API** | All transports implement `IParticle` with `Send`, `SendAsync`, `OnReceived`, `OnConnected`, and `OnDisconnected`. |
| ⚡ **Zero-Copy Messaging** | Uses `ReadOnlyMemory<byte>` and pooled buffers for minimal GC pressure. |
| 🧩 **Multiple Backends** | Supports `Inproc`, `IPC`, `TCP`, and `UDP / Multicast`. |
| 💬 **Bi-Directional Communication** | Every `IParticle` is full-duplex: send and receive simultaneously. |
| 🧵 **Thread-Safe** | Built for high concurrency using lock-free queues. |
| 🛰️ **Multicast Support** | Native UDP multicast support with TTL, loopback control, and auto configuration. |
| 🧰 **Fluent Builder DSL** | Configure and build transports using a single fluent interface (`ParticleBuilder`). |

---

## 🧱 Supported Transports

| Transport | Description | Use Case |
|------------|--------------|----------|
| 🧩 **Inproc** | In-process memory channel, zero allocations | Testing, simulations, multi-component apps |
| 💾 **IPC** | Shared-memory transport via mapped files | Cross-process, same machine |
| 🌐 **TCP** | Network transport, reliable streaming | Client-server, LAN, remote |
| 📡 **UDP** | Datagram transport, supports unicast & multicast | Telemetry, broadcast, discovery |

---

## ⚙️ Benchmark Results

All benchmarks were run using **BenchmarkDotNet** on a **.NET 9.0** build targeting **x64** in Release mode.

### 🧩 Inproc Transport (Single Process)

| Method                   | Mean     | Error    | StdDev   | Median   | Allocated |
|---------------------------|---------:|---------:|---------:|---------:|-----------:|
| 🏆 **SendAsync 10k messages** | **5.080 ms** | 7.134 ms | 4.719 ms | 2.260 ms | 956.79 KB |

**Interpretation:**  
🚀 The `Inproc` transport achieves **~2.2 ms median latency** for 10,000 asynchronous message sends,  
with **under 1 MB total allocations**, demonstrating near-zero overhead and outstanding local throughput.

---

## 🔧 Quick Start

### 1️⃣ Create a TCP echo server and client

```csharp
using Faster.Transport;
using System.Net;
using System.Text;

// 🖥️ Server
var server = new ParticleBuilder()
    .UseMode(TransportMode.Tcp)
    .BindTo(new IPEndPoint(IPAddress.Loopback, 5000))
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"Server received: {Encoding.UTF8.GetString(msg.Span)}");
        p.Send(msg.Span); // echo back
    })
    .Build();

// 💻 Client
var client = new ParticleBuilder()
    .UseMode(TransportMode.Tcp)
    .ConnectTo(new IPEndPoint(IPAddress.Loopback, 5000))
    .OnReceived((_, msg) =>
        Console.WriteLine($"Client got echo: {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

await Task.Delay(200); // wait for connect
await client.SendAsync(Encoding.UTF8.GetBytes("Hello TCP!"));
```

---

### 2️⃣ In-Process Messaging

```csharp
var server = new ParticleBuilder()
    .UseMode(TransportMode.Inproc)
    .WithChannel("local-bus", isServer: true)
    .OnReceived((_, msg) =>
        Console.WriteLine($"Server received: {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

var client = new ParticleBuilder()
    .UseMode(TransportMode.Inproc)
    .WithChannel("local-bus")
    .Build();

await client.SendAsync(Encoding.UTF8.GetBytes("Ping Inproc!"));
```

---

### 3️⃣ IPC (Shared Memory)

```csharp
var server = new ParticleBuilder()
    .UseMode(TransportMode.Ipc)
    .WithChannel("shared-map", isServer: true)
    .OnReceived((_, msg) =>
        Console.WriteLine($"IPC Server: {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

var client = new ParticleBuilder()
    .UseMode(TransportMode.Ipc)
    .WithChannel("shared-map")
    .Build();

await client.SendAsync(Encoding.UTF8.GetBytes("Hello from IPC!"));
```

---

### 4️⃣ UDP / Multicast Messaging

Now with **auto-configuring multicast** — no need to manually bind or connect!

```csharp
using Faster.Transport;
using System.Net;
using System.Text;

var group = IPAddress.Parse("239.10.10.10");
var port = 50000;

// 🛰️ Multicast Sender
var server = new ParticleBuilder()
    .EnableMulticast(group, port, disableLoopback: false)
    .Build();

// 📡 Multicast Clients
var client1 = new ParticleBuilder()
    .EnableMulticast(group, port)
    .OnReceived((_, msg) =>
        Console.WriteLine($"Client 1 got: {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

var client2 = new ParticleBuilder()
    .EnableMulticast(group, port)
    .OnReceived((_, msg) =>
        Console.WriteLine($"Client 2 got: {Encoding.UTF8.GetString(msg.Span)}"))
    .Build();

Console.WriteLine("Press ENTER to send 3 multicast messages...");
Console.ReadLine();

for (int i = 0; i < 3; i++)
{
    var msg = Encoding.UTF8.GetBytes($"Broadcast #{i + 1}");
    await server.SendAsync(msg);
    Console.WriteLine($"[Server] Sent Broadcast #{i + 1}");
    await Task.Delay(300);
}
```

✅ **Output**
```
[Server] Sent Broadcast #1
Client 1 got: Broadcast #1
Client 2 got: Broadcast #1
...
```

---

## 🧩 ParticleBuilder Overview

| Method | Description |
|---------|-------------|
| `.UseMode(TransportMode)` | Selects the backend (Tcp, Inproc, Ipc, or Udp). |
| `.BindTo(IPEndPoint)` | Binds to a local endpoint (for servers or UDP listeners). |
| `.ConnectTo(IPEndPoint)` | Connects to a remote endpoint (for clients). |
| `.EnableMulticast(IPAddress, int, bool)` | Configures and auto-binds a UDP multicast socket. |
| `.AllowBroadcast(bool)` | Enables UDP broadcast mode. |
| `.WithChannel(string, bool)` | Used by Inproc and IPC transports. |
| `.OnReceived(Action<IParticle, ReadOnlyMemory<byte>>)` | Handles incoming messages. |
| `.OnConnected(Action<IParticle>)` | Triggered when connection is ready. |
| `.OnDisconnected(Action<IParticle, Exception?>)` | Triggered when connection closes. |
| `.WithBufferSize(int)` | Sets per-operation buffer size. |
| `.WithRingCapacity(int)` | Sets internal shared memory size (IPC/Inproc). |
| `.WithParallelism(int)` | Sets degree of parallelism. |

---

## 🧪 Testing UDP Multicast Locally

If messages are not received:

1. Ensure `disableLoopback: false` in `.EnableMulticast()` for local tests.  
2. Disable the firewall or open UDP port `50000`.  
3. Verify via Wireshark: filter `udp.port == 50000`.  
4. Loopback works only if `MulticastLoopback` is enabled.

---

## ⚙️ Requirements

| Requirement | Minimum |
|--------------|----------|
| .NET | **.NET 9.0** or newer |
| OS | Windows, Linux, macOS |
| Recommended | .NET 8+ for maximum performance |

---

## 🧑‍💻 Example Projects

| Project | Description |
|----------|-------------|
| `Faster.Transport.Demo` | Contains TCP, Inproc, IPC, and UDP demos |
| `Faster.Transport.Tests` | Includes integration & multicast test suite |
| `Faster.Transport.Primitives` | Zero-copy buffer and ring-based queue utilities |

---

## 🧰 License

MIT License © 2025 — Designed for **speed, simplicity, and reliability.**
