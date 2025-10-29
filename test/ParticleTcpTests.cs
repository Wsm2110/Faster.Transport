using Faster.Transport.Features.Tcp;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Faster.Transport.Unittests;

[CollectionDefinition("SequentialTests", DisableParallelization = true)]
public class ParticleTcpTests : IDisposable
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

    [Fact(DisplayName = "Particle client can exchange small messages with Reactor")]
    public async Task Particle_Client_Should_Exchange_Small_Message_With_Reactor()
    {
        string received = "";
        string echoed = "";

        var mre = new ManualResetEvent(false);
        using var reactor = new Reactor(new IPEndPoint(IPAddress.Loopback, _port));
        reactor.OnConnected = conn =>
        {
            mre.Set();
            Console.WriteLine("Server: client connected");
        };
        reactor.OnReceived = (conn, data) =>
        {
            received = Encoding.UTF8.GetString(data.Span);
            conn.Send(Encoding.UTF8.GetBytes("pong"));
        };
        reactor.Start();

        var client = new Particle(new IPEndPoint(IPAddress.Loopback, _port));
        client.OnReceived = (p, data) => echoed = Encoding.UTF8.GetString(data.Span);

        mre.WaitOne();

        client.Send(Encoding.UTF8.GetBytes("ping"));
        await Task.Delay(300, _cts.Token);

        Assert.Equal("ping", received);
        Assert.Equal("pong", echoed);
    }

    [Fact(DisplayName = "Large payloads should be transmitted correctly")]
    public async Task Particle_Should_Transmit_Large_Payloads()
    {
        byte[] payload = new byte[4096];
        new Random(123).NextBytes(payload);
        byte[]? echoed = null;

        var port = GetFreeTcpPort();

        using var reactor = new Reactor(new IPEndPoint(IPAddress.Loopback, port));
        reactor.OnReceived = (client, data) => client.Send(data.Span);
        reactor.Start();

        var client = new Particle(new IPEndPoint(IPAddress.Loopback, port));
        client.OnReceived = (p, data) => echoed = data.ToArray();

        await client.SendAsync(payload);
        await Task.Delay(300, _cts.Token);

        Assert.NotNull(echoed);
        Assert.True(echoed!.SequenceEqual(payload));
    }

    [Fact(DisplayName = "Sending payload larger than buffer should throw")]
    public void Should_Throw_On_Payload_Too_Large()
    {
        var port = GetFreeTcpPort();
        using var reactor = new Reactor(new IPEndPoint(IPAddress.Loopback, port));
        reactor.Start();

        var client = new Particle(new IPEndPoint(IPAddress.Loopback, port), bufferSize: 1024);

        var tooLarge = new byte[4096];
        Assert.Throws<ArgumentOutOfRangeException>(() => client.Send(tooLarge));
    }


    [Fact(DisplayName = "Reactor should handle multiple small messages")]
    public async Task Reactor_Should_Handle_Multiple_Messages()
    {
        int count = 0;

        var port = GetFreeTcpPort();
        var mre = new ManualResetEvent(false);

        using var reactor = new Reactor(new IPEndPoint(IPAddress.Loopback, port));
        reactor.OnReceived = (conn, data) => Interlocked.Increment(ref count);
        reactor.Start();

        reactor.OnConnected = con =>
        {
            mre.Set();
        };


        var client = new Particle(new IPEndPoint(IPAddress.Loopback, port));
        byte[] msg = Encoding.UTF8.GetBytes("ping");

        mre.WaitOne();

        for (int i = 0; i < 10; i++)
            client.Send(msg);

        await Task.Delay(300, _cts.Token);

        Assert.Equal(10, count);
    }
}