using Faster.Transport;
using Faster.Transport.Contracts;
using System.Text;

namespace Faster.Transport.Unittests;

[CollectionDefinition("SequentialTests", DisableParallelization = true)]
public class InprocParticleTests : IDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(5));
    public void Dispose() => _cts.Dispose();

    private static string Channel() => "inproc-" + Guid.NewGuid().ToString("N");
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static string Str(ReadOnlyMemory<byte> m) => Encoding.UTF8.GetString(m.Span);

    // ------------------------------------------------------------
    // 1. Basic round-trip (client → server → client)
    // ------------------------------------------------------------
    [Fact(DisplayName = "Basic round-trip message exchange (builder)")]
    public async Task Basic_RoundTrip()
    {
        string name = Channel();

        // Reactor (server)
        var server = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name, isServer: true)
            .Build();

        // Client
        var client = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .Build();

        string? serverReceived = null;
        string? clientReceived = null;

        server.OnReceived = (p, data) =>
        {
            serverReceived = Str(data);
            p.Send(Utf8("pong"));
        };

        client.OnReceived = (p, data) =>
        {
            clientReceived = Str(data);
        };

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
    [Fact(DisplayName = "Large payload is transferred correctly (builder)")]
    public async Task Large_Payload_Transferred()
    {
        string name = Channel();

        var server = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name, isServer: true)
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .Build();

        byte[] sent = new byte[128 * 1024];
        new Random(123).NextBytes(sent);
        byte[]? received = null;

        server.OnReceived = (p, data) => p.Send(data.Span);
        client.OnReceived = (p, data) => received = data.ToArray();

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
    [Fact(DisplayName = "Zero-length payload is ignored (builder)")]
    public async Task Zero_Length_Ignored()
    {
        string name = Channel();

        var server = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name, isServer: true)
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .Build();

        bool got = false;
        server.OnReceived = (p, data) =>
        {
            got = true;
        };

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
    [Fact(DisplayName = "Multiple messages preserve order (builder)")]
    public async Task Messages_Preserve_Order()
    {
        string name = Channel();

        var server = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name, isServer: true)
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .Build();

        var received = new List<int>();

        server.OnReceived = (p, data) =>
        {
            var num = int.Parse(Str(data));
            received.Add(num);
        };

        await Task.Delay(100, _cts.Token);
        for (int i = 0; i < 50; i++)
            client.Send(Utf8(i.ToString()));

        await Task.Delay(300, _cts.Token);
        Assert.Equal(Enumerable.Range(0, 50), received);

        server.Dispose();
        client.Dispose();
    }

    // ------------------------------------------------------------
    // 5. Concurrent Send() calls (backpressure & MPSC safety)
    // ------------------------------------------------------------
    [Fact(DisplayName = "Concurrent Send() calls are safe (builder)")]
    public async Task Concurrent_Sends_Are_Safe()
    {
        string name = Channel();

        var server = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name, isServer: true)
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .Build();

        int count = 0;
        server.OnReceived = (p, data) => Interlocked.Increment(ref count);

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
    [Fact(DisplayName = "OnDisconnected is triggered on close (builder)")]
    public async Task OnDisconnected_Fires()
    {
        string name = Channel();

        var server = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name, isServer: true)
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .Build();

        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnDisconnected = _ => disconnected.TrySetResult(true);

        await Task.Delay(100, _cts.Token);
        client.Dispose();

        var result = await disconnected.Task.WaitAsync(_cts.Token);
        Assert.True(result);

        server.Dispose();
    }

    // ------------------------------------------------------------
    // 7. OnConnected triggers for client automatically
    // ------------------------------------------------------------
    [Fact(DisplayName = "OnConnected triggers for client (builder)")]
    public async Task OnConnected_Fires_For_Client()
    {
        string name = Channel();

        var server = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name, isServer: true)
            .Build();

        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .OnConnected(_ =>
            {
                connected.TrySetResult(true);
            })
            .Build();

        var ok = await connected.Task.WaitAsync(_cts.Token);
        Assert.True(ok);

        server.Dispose();
        client.Dispose();
    }

    // ------------------------------------------------------------
    // 8. Dispose prevents future sends
    // ------------------------------------------------------------
    [Fact(DisplayName = "Dispose prevents sending (builder)")]
    public void Dispose_Prevents_Send()
    {
        string name = Channel();

        var server = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name, isServer: true)
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .Build();

        client.Dispose();
        Assert.Throws<ObjectDisposedException>(() => client.Send(Utf8("x")));

        server.Dispose();
    }

    // ------------------------------------------------------------
    // 9. Multiple clients can connect to one reactor
    // ------------------------------------------------------------
    [Fact(DisplayName = "Multiple clients connect to same reactor (builder)")]
    public async Task Multiple_Clients_Work()
    {
        string name = Channel();

        var server = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name, isServer: true)
            .Build();

        int received = 0;
        server.OnReceived = (p, data) => Interlocked.Increment(ref received);

        var clients = Enumerable.Range(0, 3)
            .Select(_ => new ParticleBuilder()
                .UseMode(TransportMode.Inproc)
                .WithChannel(name)
                .Build())
            .ToList();

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
