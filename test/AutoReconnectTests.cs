using Faster.Transport.Contracts;
using Faster.Transport.Features.Tcp;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Faster.Transport.Unittests;

[CollectionDefinition("SequentialTests", DisableParallelization = true)]

public class AutoReconnectTests : IDisposable
{
    private readonly int _port;
    private Reactor? _server;
    private readonly ConcurrentBag<string> _events = new();

    public AutoReconnectTests()
    {
        _port = new Random().Next(40000, 50000);
    }

    [Fact(DisplayName = "AutoReconnect client retries until server starts")]
    public async Task AutoReconnect_ClientRetriesUntilServerOnline()
    {

        // 1️⃣ Create client before server exists (expected to fail first)
        IParticle? client = null;

        try
        {
            client = new ParticleBuilder()
                .UseMode(TransportMode.Tcp)
                .WithRemote(new IPEndPoint(IPAddress.Loopback, _port))
                .WithAutoReconnect(baseSeconds: 1, maxSeconds: 10)
                .OnConnected(p => _events.Add("Connected"))
                .OnDisconnected(p => _events.Add("Disconnected"))
                .OnReceived((p, msg) => _events.Add("Received"))
                .Build();
        }
        catch (SocketException ex)
        {
            // ✅ swallow initial "connection refused" — expected before server starts
            Console.WriteLine($"[Test] Expected connect failure: {ex.Message}");
        }

        // 2️⃣ Wait to show it's retrying (server is offline)
        await Task.Delay(1000);
        Assert.DoesNotContain("Connected", _events);

        // 3️⃣ Start the server
        StartServer();

        // 4️⃣ Wait for reconnect
        await Task.Delay(2500);
        Assert.Contains("Connected", _events);

        // 5️⃣ Stop server to trigger disconnect
        StopServer();

        await Task.Delay(1000);
        Assert.Contains("Disconnected", _events);

        // 6️⃣ Restart server to allow reconnect
        StartServer();
        await Task.Delay(2000);

        // Expect a second connection event
        Assert.True(_events.Count(e => e == "Connected") >= 2);
    }

    [Fact(DisplayName = "Client can send/receive after reconnect")]
    public async Task AutoReconnect_ClientCanSendAfterReconnect()
    {
        var received = new ConcurrentBag<string>();

        // Create client
        var client = new ParticleBuilder()
            .UseMode(TransportMode.Tcp)
            .WithRemote(new IPEndPoint(IPAddress.Loopback, _port))
            .WithAutoReconnect(baseSeconds: 1, maxSeconds: 10)
            .OnConnected(p => _events.Add("Connected"))
            .OnDisconnected(p => _events.Add("Disconnected"))
            .OnReceived((p, msg) => received.Add(System.Text.Encoding.UTF8.GetString(msg.Span)))
            .Build();

        StartServer();

        // Wait for connection
        await Task.Delay(1000);

        // Send message to server
        var payload = System.Text.Encoding.UTF8.GetBytes("ping");
        client.Send(payload);

        await Task.Delay(500);
        Assert.Contains(received, s => s == "pong");
    }

    [Fact(DisplayName = "Server handles multiple reconnecting clients")]
    public async Task AutoReconnect_MultipleClientsReconnect()
    {
        StartServer();
        int clientCount = 3;

        var connected = new ConcurrentBag<int>();

        for (int i = 0; i < clientCount; i++)
        {
            int id = i;
            new ParticleBuilder()
                .UseMode(TransportMode.Tcp)
                .WithRemote(new IPEndPoint(IPAddress.Loopback, _port))
                .WithAutoReconnect(baseSeconds: 0.1, maxSeconds: 1)
                .OnConnected(p =>
                {
                    _events.Add($"Client{id}_Connected");
                    connected.Add(id);
                })
                .Build();
        }

        await Task.Delay(2500);
        Assert.Equal(clientCount, connected.Count);

        // Restart server
        StopServer();
        await Task.Delay(1000);
        StartServer();

        await Task.Delay(2000);
        Assert.True(_events.Count(e => e.Contains("Client") && e.EndsWith("_Connected")) >= clientCount * 2);
    }

    private void StartServer()
    {
        if (_server != null)
            return;

        var local = new IPEndPoint(IPAddress.Loopback, _port);
        _server = new Reactor(local);

        _server.OnConnected = c => _events.Add("Server:Connected");
        _server.OnReceived = (p, msg) =>
        {
            // echo “pong”
            var str = System.Text.Encoding.UTF8.GetString(msg.Span);
            if (str == "ping")
                p.Send(System.Text.Encoding.UTF8.GetBytes("pong"));
        };

        _server.Start();
        _events.Add("ServerStarted");
    }

    private void StopServer()
    {
        if (_server == null) return;
        _server.Stop();
        _events.Add("ServerStopped");
        _server = null;
    }

    public void Dispose()
    {
        StopServer();
    }
}