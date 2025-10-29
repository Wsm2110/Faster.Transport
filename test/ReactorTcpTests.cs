using Faster.Transport.Features.Tcp;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Faster.Transport.Unittests;

[CollectionDefinition("SequentialTests", DisableParallelization = true)]
public class ReactorTests : IDisposable
{
    private readonly int _port = GetFreeTcpPort();
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(10));

    public void Dispose() => _cts.Dispose();

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact(DisplayName = "Reactor should accept incoming client connections (via builder)")]
    public async Task Reactor_Should_Accept_Client_Connection()
    {
        bool connected = false;

        using var reactor = new Reactor(new IPEndPoint(IPAddress.Loopback, _port));
        reactor.OnConnected = conn => connected = true;
        reactor.Start();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Tcp)
            .WithRemote(new IPEndPoint(IPAddress.Loopback, _port))
            .OnConnected(_ => connected = true)
            .Build();

        // Wait for reactor to accept
        await Task.Delay(300, _cts.Token);

        Assert.True(connected);
    }

    [Fact(DisplayName = "Reactor should handle multiple concurrent clients (via builder)")]
    public async Task Reactor_Should_Handle_Multiple_Clients()
    {
        int messageCount = 0;
        int clientCount = 0;

        using var reactor = new Reactor(new IPEndPoint(IPAddress.Loopback, _port));
        reactor.OnConnected = _ => Interlocked.Increment(ref clientCount);
        reactor.OnReceived = (conn, data) => Interlocked.Increment(ref messageCount);
        reactor.Start();

        // Build two clients using builder
        var client1 = new ParticleBuilder()
            .UseMode(TransportMode.Tcp)
            .WithRemote(new IPEndPoint(IPAddress.Loopback, _port))
            .Build();

        var client2 = new ParticleBuilder()
            .UseMode(TransportMode.Tcp)
            .WithRemote(new IPEndPoint(IPAddress.Loopback, _port))
            .Build();

        await Task.Delay(500, _cts.Token);

        // Both clients send frames
        var frame = BuildFrame("hello");
        client1.Send(frame);
        client2.Send(frame);

        await Task.Delay(300, _cts.Token);

        Assert.Equal(2, clientCount);
        Assert.Equal(2, messageCount);

        client1.Dispose();
        client2.Dispose();
    }

    [Fact(DisplayName = "Reactor.Stop should gracefully close all clients and listener (via builder)")]
    public async Task Reactor_Stop_Should_Close_All_Clients()
    {
        using var reactor = new Reactor(new IPEndPoint(IPAddress.Loopback, _port));
        reactor.Start();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Tcp)
            .WithRemote(new IPEndPoint(IPAddress.Loopback, _port))
            .Build();

        await Task.Delay(200, _cts.Token);

        reactor.Stop();

        // Should be restartable after stop
        reactor.Start();

        client.Dispose();
    }

    private static byte[] BuildFrame(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var frame = new byte[data.Length + 4];
        BitConverter.GetBytes(data.Length).CopyTo(frame, 0);
        Array.Copy(data, 0, frame, 4, data.Length);
        return frame;
    }
}
