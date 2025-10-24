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

    [Fact(DisplayName = "Reactor should accept incoming client connections")]
    public async Task Reactor_Should_Accept_Client_Connection()
    {
        bool connected = false;

        using var reactor = new Reactor(new IPEndPoint(IPAddress.Loopback, _port));
        reactor.OnConnected = conn => connected = true;
        reactor.Start();

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, _port);

        await Task.Delay(200, _cts.Token);
        Assert.True(connected);
    }

    [Fact(DisplayName = "Reactor should handle multiple concurrent clients")]
    public async Task Reactor_Should_Handle_Multiple_Clients()
    {
        int messageCount = 0;

        using var reactor = new Reactor(new IPEndPoint(IPAddress.Loopback, _port));
        reactor.OnReceived = (conn, data) => Interlocked.Increment(ref messageCount);
        reactor.Start();

        var c1 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var c2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        await c1.ConnectAsync(IPAddress.Loopback, _port);
        await c2.ConnectAsync(IPAddress.Loopback, _port);

        await Task.Delay(200, _cts.Token);

        byte[] msg = BuildFrame("hello");
        c1.Send(msg);
        c2.Send(msg);

        await Task.Delay(300, _cts.Token);

        Assert.Equal(2, messageCount);
    }

    [Fact(DisplayName = "Reactor should cleanup disconnected clients")]
    public async Task Reactor_Should_Cleanup_Disconnected_Clients()
    {
        int disconnectedCount = 0;

        using var reactor = new Reactor(new IPEndPoint(IPAddress.Loopback, _port));
        reactor.OnConnected = conn =>
        {
            conn.OnDisconnected = _ => Interlocked.Increment(ref disconnectedCount);
        };
        reactor.Start();

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, _port);
        socket.Close();

        await Task.Delay(300, _cts.Token);
        Assert.Equal(1, disconnectedCount);
    }

    [Fact(DisplayName = "Reactor.Stop should gracefully close all clients and listener")]
    public async Task Reactor_Stop_Should_Close_All_Clients()
    {
        using var reactor = new Reactor(new IPEndPoint(IPAddress.Loopback, _port));
        reactor.Start();

        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, _port);

        reactor.Stop();

        // Should be restartable after stop
        reactor.Start();
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