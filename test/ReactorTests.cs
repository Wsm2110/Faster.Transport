using Faster.Transport.Contracts;
using Faster.Transport.Features.Tcp;
using Faster.Transport.Inproc;
using Faster.Transport.Ipc;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Faster.Transport.Unittests;

/// <summary>
/// Tests for verifying <see cref="ReactorBuilder"/> behavior across TCP, IPC, and Inproc modes.
/// </summary>
/// <remarks>
/// Each test starts a reactor, connects one or more clients,
/// and ensures message round-trips and connection events work correctly.
/// </remarks>
[CollectionDefinition("SequentialTests", DisableParallelization = true)]
public sealed class ReactorBuilderTests : IDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(5));
    public void Dispose() => _cts.Dispose();

    private static string Channel() => "reactor-" + Guid.NewGuid().ToString("N");
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string Str(ReadOnlyMemory<byte> data) => Encoding.UTF8.GetString(data.Span);

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        Assert.True(predicate(), $"Condition failed within {timeoutMs}ms");
    }

    // ------------------------------------------------------------
    // 1. Inproc reactor basic round-trip
    // ------------------------------------------------------------
    [Fact(DisplayName = "Inproc reactor round-trip works")]
    public async Task Inproc_Reactor_RoundTrip_Works()
    {
        string name = Channel();
        string? serverReceived = null;
        string? clientReceived = null;

        var reactor = new ReactorBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .OnReceived((p, data) =>
            {
                serverReceived = Str(data);
                p.Send(Bytes("pong"));
            })
            .Build();

        reactor.Start();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .OnReceived((_, data) => clientReceived = Str(data))
            .Build();

        await Task.Delay(100, _cts.Token);
        client.Send(Bytes("ping"));
        await WaitUntilAsync(() => serverReceived == "ping" && clientReceived == "pong");

        reactor.Dispose();
        client.Dispose();
    }

    // ------------------------------------------------------------
    // 2. IPC reactor basic round-trip
    // ------------------------------------------------------------
    [Fact(DisplayName = "IPC reactor round-trip works")]
    public async Task Ipc_Reactor_RoundTrip_Works()
    {
        string name = Channel();
        string? serverReceived = null;
        string? clientReceived = null;

        var mre = new ManualResetEventSlim(false);

        var reactor = new ReactorBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .OnReceived((p, data) =>
            {
                serverReceived = Str(data);
                p.Send(Bytes("pong"));
            })
            .OnConnected(p =>
            {
                mre.Set();
            })
            .Build();

        reactor.Start();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(name)
            .OnReceived((_, data) =>
            {
                clientReceived = Str(data);
            })
            .Build();

        mre.Wait();

        client.Send(Bytes("ping"));

        await WaitUntilAsync(() => serverReceived == "ping" && clientReceived == "pong");

        reactor.Dispose();
        client.Dispose();
    }

    // ------------------------------------------------------------
    // 3. TCP reactor basic round-trip (loopback)
    // ------------------------------------------------------------
    [Fact(DisplayName = "TCP reactor round-trip works (loopback)")]
    public async Task Tcp_Reactor_RoundTrip_Works()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, GetEphemeralPort());
        string? serverReceived = null;
        string? clientReceived = null;

        var mre = new ManualResetEventSlim(false);

        var reactor = new ReactorBuilder()
            .UseMode(TransportMode.Tcp)
            .BindTo(endpoint)
            .OnReceived((p, data) =>
            {
                serverReceived = Str(data);
                p.Send(Bytes("pong"));
            })
            .OnConnected(p => { mre.Set(); })
            .Build();

        reactor.Start();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Tcp)
            .WithRemote(endpoint)
            .OnReceived((_, data) => clientReceived = Str(data))
            .Build();

        mre.Wait();
        client.Send(Bytes("ping"));
        await WaitUntilAsync(() => serverReceived == "ping" && clientReceived == "pong");

        reactor.Dispose();
        client.Dispose();
    }

    // ------------------------------------------------------------
    // 4. OnConnected triggers properly
    // ------------------------------------------------------------
    [Fact(DisplayName = "Reactor triggers OnConnected when client connects")]
    public async Task Reactor_OnConnected_Fires()
    {
        string name = Channel();
        bool connected = false;

        var reactor = new ReactorBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .OnConnected(_ => connected = true)
            .Build();

        reactor.Start();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .Build();

        await WaitUntilAsync(() => connected);
        reactor.Dispose();
        client.Dispose();
    }

    // ------------------------------------------------------------
    // 5. Multiple clients connect to a single reactor
    // ------------------------------------------------------------
    [Fact(DisplayName = "Multiple clients connect to same reactor")]
    public async Task Reactor_Multiple_Clients_Work()
    {
        string name = Channel();
        int connected = 0;
        int received = 0;

        var reactor = new ReactorBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel(name)
            .OnConnected(_ => Interlocked.Increment(ref connected))
            .OnReceived((_, data) => Interlocked.Increment(ref received))
            .Build();

        reactor.Start();

        var clients = Enumerable.Range(0, 3)
            .Select(_ => new ParticleBuilder()
                .UseMode(TransportMode.Inproc)
                .WithChannel(name)
                .Build())
            .ToList();

        await WaitUntilAsync(() => connected == 3);
        foreach (var cli in clients)
            cli.Send(Bytes("msg"));

        await WaitUntilAsync(() => received == 3);

        foreach (var cli in clients)
            cli.Dispose();

        reactor.Dispose();
    }

    // ------------------------------------------------------------
    // Helper: Get a free ephemeral TCP port
    // ------------------------------------------------------------
    private static int GetEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
