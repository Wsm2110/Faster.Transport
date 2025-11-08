using System.Collections.Concurrent;
using System.Text;
using Faster.Transport.Contracts;

namespace Faster.Transport.Unittests;

/// <summary>
/// Unit tests for verifying IPC (Inter-Process Communication) behavior using
/// <see cref="ReactorBuilder"/> for servers and <see cref="ParticleBuilder"/> for clients.
/// </summary>
/// <remarks>
/// These tests ensure correct message delivery, connection handling, and isolation
/// between multiple IPC clients.
/// </remarks>
[CollectionDefinition("SequentialTests", DisableParallelization = true)]
public sealed class IpcBuilderTests
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(5));
    public void Dispose() => _cts.Dispose();

    private static string Channel() => "ipc-" + Guid.NewGuid().ToString("N");
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static string Str(ReadOnlyMemory<byte> m) => Encoding.UTF8.GetString(m.Span);

    private static string NewChannel() => $"FasterIpc_{Guid.NewGuid():N}";
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout, int spinMs = 5)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(spinMs);
        }
        return condition();
    }

    // ------------------------------------------------------------
    // 1. One client connects and round-trips a message
    // ------------------------------------------------------------
    [Fact(DisplayName = "One client connects and round-trips message")]
    public async Task OneClient_connects_and_roundtrips()
    {
        var channel = NewChannel();
        var serverReceived = new ConcurrentQueue<(IParticle From, byte[] Payload)>();

        // 🧱 Build the server using ReactorBuilder
        var server = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(channel)
            .OnConnected(_ => { /* optional logging */ })
            .OnReceived((p, data) =>
            {
                var arr = data.ToArray();
                serverReceived.Enqueue((p, arr));
                // Echo message back to sender
                p.Send(arr);
            })
            .Build();

        server.Start();

        try
        {
            // 🧱 Build the client using ParticleBuilder
            var clientReceived = new ConcurrentQueue<byte[]>();
            var client = new ParticleBuilder()
                .UseMode(TransportMode.Ipc)
                .WithChannel(channel)
                .OnReceived((_, data) => clientReceived.Enqueue(data.ToArray()))
                .Build();

            try
            {
                var payload = Bytes("ping-123");
                client.Send(payload);

                await Task.Delay(2000);

                // Wait for server to receive message
                Assert.True(WaitUntil(() => serverReceived.Count == 1, TimeSpan.FromSeconds(2)), "Server did not receive payload in time");

                var okServer = serverReceived.TryDequeue(out var srvMsg);
                Assert.True(okServer);
                Assert.Equal(payload, srvMsg.Payload);

                // Wait for client echo
                Assert.True(WaitUntil(() => clientReceived.Count == 1, TimeSpan.FromSeconds(2)), "Client did not receive echo in time");

                var okCli = clientReceived.TryDequeue(out var back);
                Assert.True(okCli);
                Assert.Equal(payload, back);
            }
            finally
            {
                client.Dispose();
            }
        }
        finally
        {
            server.Dispose();
        }
    }

    // ------------------------------------------------------------
    // 2. Two clients receive broadcast from server
    // ------------------------------------------------------------
    [Fact(DisplayName = "Two clients receive broadcast from server")]
    public void TwoClients_broadcast_from_server()
    {
        var channel = NewChannel();
        var connected = new ConcurrentBag<IParticle>();

        // 🧱 Build server that tracks connected clients
        var server = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(channel)
            .OnConnected(p => connected.Add(p))
            .OnReceived((_, _) => { /* ignore */ })
            .Build();

        server.Start();

        try
        {
            var receivedA = new ConcurrentQueue<byte[]>();
            var receivedB = new ConcurrentQueue<byte[]>();

            var clientA = new ParticleBuilder()
                .UseMode(TransportMode.Ipc)
                .WithChannel(channel)
                .OnReceived((_, data) => receivedA.Enqueue(data.ToArray()))
                .Build();

            var clientB = new ParticleBuilder()
                .UseMode(TransportMode.Ipc)
                .WithChannel(channel)
                .OnReceived((_, data) => receivedB.Enqueue(data.ToArray()))
                .Build();

            try
            {
                // Wait until both clients are registered
                Assert.True(WaitUntil(() => connected.Count >= 2, TimeSpan.FromSeconds(3)), "Server did not detect two clients in time");

                var payload = Bytes("hello-all");

                // Server sends message to all connected clients
                foreach (var cli in connected)
                    cli.Send(payload);

                Assert.True(WaitUntil(() => receivedA.Count == 1 && receivedB.Count == 1, TimeSpan.FromSeconds(2)));

                Assert.True(receivedA.TryDequeue(out var a));
                Assert.True(receivedB.TryDequeue(out var b));

                Assert.Equal(payload, a);
                Assert.Equal(payload, b);
            }
            finally
            {
                clientA.Dispose();
                clientB.Dispose();
            }
        }
        finally
        {
            server.Dispose();
        }
    }

    // ------------------------------------------------------------
    // 3. Multiple clients send independently without cross-talk
    // ------------------------------------------------------------
    [Fact(DisplayName = "Multiple clients send independently and do not receive others' messages")]
    public async Task MultipleClients_send_individually_and_do_not_receive_others()
    {
        var channel = NewChannel();
        var serverReceived = new ConcurrentQueue<(IParticle From, byte[] Payload)>();

        // 🧱 Build server
        var server = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(channel)
            .OnReceived((p, data) =>
            {
                var arr = data.ToArray();
                serverReceived.Enqueue((p, arr));
                // Echo back only to the sender
                p.Send(arr);
            })
            .Build();

        server.Start();

        try
        {
            var clientAReceived = new ConcurrentQueue<byte[]>();
            var clientBReceived = new ConcurrentQueue<byte[]>();

            var clientA = new ParticleBuilder()
                .UseMode(TransportMode.Ipc)
                .WithChannel(channel)
                .OnReceived((_, data) =>
                {
                    clientAReceived.Enqueue(data.ToArray());
                })
                .Build();

            var clientB = new ParticleBuilder()
                .UseMode(TransportMode.Ipc)
                .WithChannel(channel)
                .OnReceived((_, data) =>
                {
                    clientBReceived.Enqueue(data.ToArray());
                })
                .Build();

            try
            {
                var payloadA = Bytes("from-A");
                var payloadB = Bytes("from-B");

                // Each client sends its own message
                clientA.Send(payloadA);
                clientB.Send(payloadB);

                await Task.Delay(TimeSpan.FromSeconds(2));

                // Server should get both messages
                Assert.True(serverReceived.Count == 2);

                // Each client should receive only its own echo
                Assert.True(WaitUntil(() => clientAReceived.Count == 1, TimeSpan.FromSeconds(2)), "Client A did not receive echo");
                Assert.True(WaitUntil(() => clientBReceived.Count == 1, TimeSpan.FromSeconds(2)), "Client B did not receive echo");

                Assert.True(clientAReceived.TryDequeue(out var echoA));
                Assert.True(clientBReceived.TryDequeue(out var echoB));

                Assert.Equal(payloadA, echoA);
                Assert.Equal(payloadB, echoB);

                // Verify no cross-contamination
                Assert.Empty(clientAReceived);
                Assert.Empty(clientBReceived);
            }
            finally
            {
                clientA.Dispose();
                clientB.Dispose();
            }
        }
        finally
        {
            server.Dispose();
        }
    }



    // ------------------------------------------------------------
    // 1. Basic round-trip (client → server → client)
    // ------------------------------------------------------------
    [Fact(DisplayName = "IPC: Basic round-trip message exchange (builder)")]
    public async Task Basic_RoundTrip()
    {
        string name = Channel();
        string? serverReceived = null;
        string? clientReceived = null;

        var server = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .OnReceived((p, data) =>
            {
                serverReceived = Str(data);
                p.Send(Utf8("pong"));
            })
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .OnReceived((_, data) => clientReceived = Str(data))
            .Build();

        server.Start();
        await Task.Delay(100, _cts.Token);
        client.Send(Utf8("ping"));
        await Task.Delay(200, _cts.Token);

        Assert.Equal("ping", serverReceived);
        Assert.Equal("pong", clientReceived);

        server.Dispose();
        client.Dispose();
    }

    // ------------------------------------------------------------
    // 2. Large payload delivery
    // ------------------------------------------------------------
    [Fact(DisplayName = "IPC: Large payload is transferred correctly (builder)")]
    public async Task Large_Payload_Transferred()
    {
        string name = Channel();
        byte[] sent = new byte[128 * 1024];
        new Random(123).NextBytes(sent);
        byte[]? received = null;

        var server = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .OnReceived((p, data) => p.Send(data.Span))
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .OnReceived((_, data) => received = data.ToArray())
            .Build();

        server.Start();
        await Task.Delay(100, _cts.Token);
        client.Send(sent);
        await Task.Delay(200, _cts.Token);

        Assert.NotNull(received);
        Assert.True(received!.SequenceEqual(sent));

        server.Dispose();
        client.Dispose();
    }

    // ------------------------------------------------------------
    // 3. Zero-length messages are ignored
    // ------------------------------------------------------------
    [Fact(DisplayName = "IPC: Zero-length payload is ignored (builder)")]
    public async Task Zero_Length_Ignored()
    {
        string name = Channel();
        bool got = false;

        var server = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .OnReceived((_, _) => got = true)
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .Build();

        server.Start();
        await Task.Delay(100, _cts.Token);
        client.Send(ReadOnlySpan<byte>.Empty);
        await Task.Delay(100, _cts.Token);

        Assert.False(got);

        server.Dispose();
        client.Dispose();
    }

    // ------------------------------------------------------------
    // 4. Multiple messages preserve order
    // ------------------------------------------------------------
    [Fact(DisplayName = "IPC: Multiple messages preserve order (builder)")]
    public async Task Messages_Preserve_Order()
    {
        string name = Channel();
        var received = new List<byte>();

        var server = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .OnReceived((_, data) => received.Add(data.Span[0]))
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .Build();

        server.Start();
        await Task.Delay(100, _cts.Token);

        var test = new List<byte>();
        for (byte i = 0; i < 50; i++)
        {
            test.Add(i);
            client.Send(new[] { i });
        }

        await Task.Delay(300, _cts.Token);
        Assert.Equal(test, received);

        server.Dispose();
        client.Dispose();
    }

    // ------------------------------------------------------------
    // 5. Concurrent Send() calls (backpressure & MPSC safety)
    // ------------------------------------------------------------
    [Fact(DisplayName = "IPC: Concurrent Send() calls are safe (builder)")]
    public async Task Concurrent_Sends_Are_Safe()
    {
        string name = Channel();
        int count = 0;

        var server = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .OnReceived((_, _) => Interlocked.Increment(ref count))
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .Build();

        server.Start();
        await Task.Delay(100, _cts.Token);

        var tasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            for (int i = 0; i < 100; i++)
                client.Send(Utf8("msg"));
            await Task.Yield();
        });

        await Task.WhenAll(tasks);
        await Task.Delay(300, _cts.Token);

        Assert.Equal(400, count);

        server.Dispose();
        client.Dispose();
    }

    // ------------------------------------------------------------
    // 6. OnDisconnected fires when closed
    // ------------------------------------------------------------
    [Fact(DisplayName = "IPC: OnDisconnected is triggered on close (builder)")]
    public async Task OnDisconnected_Fires()
    {
        string name = Channel();
        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .OnDisconnected(_ => disconnected.TrySetResult(true))
            .Build();

        server.Start();
        await Task.Delay(100, _cts.Token);
        client.Dispose();

        var result = await disconnected.Task.WaitAsync(_cts.Token);
        Assert.True(result);

        server.Dispose();
    }

    // ------------------------------------------------------------
    // 7. OnConnected triggers for client automatically
    // ------------------------------------------------------------
    [Fact(DisplayName = "IPC: OnConnected triggers for client (builder)")]
    public async Task OnConnected_Fires_For_Client()
    {
        string name = Channel();
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .OnConnected(_ => connected.TrySetResult(true))
            .Build();

        server.Start();
        var ok = await connected.Task.WaitAsync(_cts.Token);
        Assert.True(ok);

        server.Dispose();
        client.Dispose();
    }

    // ------------------------------------------------------------
    // 8. Dispose prevents future sends
    // ------------------------------------------------------------
    [Fact(DisplayName = "IPC: Dispose prevents sending (builder)")]
    public void Dispose_Prevents_Send()
    {
        string name = Channel();

        var server = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .Build();

        server.Start();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.Send(Utf8("x")));
        server.Dispose();
    }

    // ------------------------------------------------------------
    // 9. Multiple clients can connect to one reactor
    // ------------------------------------------------------------
    [Fact(DisplayName = "IPC: Multiple clients connect to same reactor (builder)")]
    public async Task Multiple_Clients_Work()
    {
        string name = Channel();
        int received = 0;

        var server = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .OnReceived((_, _) => Interlocked.Increment(ref received))
            .Build();

        var clients = Enumerable.Range(0, 3)
            .Select(_ => new ParticleBuilder()
                .UseMode(TransportMode.Ipc)
                .WithChannel(name)
                .Build())
            .ToList();

        server.Start();
        await Task.Delay(100, _cts.Token);

        foreach (var c in clients)
            c.Send(Utf8("hello"));

        await Task.Delay(200, _cts.Token);
        Assert.Equal(3, received);

        foreach (var c in clients)
            c.Dispose();
        server.Dispose();
    }
}