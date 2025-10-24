using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Faster.Transport.Features.Tcp;
using Xunit;

public class ParticleTcpTests
{
    [Fact]
    public async Task Particle_CanSendAndReceiveData()
    {
        var serverReceived = new TaskCompletionSource<byte[]>();
        var server = new Reactor(new IPEndPoint(IPAddress.Loopback, 0));
        server.OnReceived = (client, payload) =>
        {
            serverReceived.SetResult(payload.ToArray());
            client.Return(payload.Span);
        };
        server.Start();

        var endpoint = server.LocalEndPoint!;
        var clientReceived = new TaskCompletionSource<byte[]>();
        var client = new ParticleBuilder()
            .ConnectTo(endpoint)
            .OnReceived(data => clientReceived.SetResult(data.ToArray()))
            .Build();

        // Wait for connection
        await Task.Delay(200);

        var message = Encoding.UTF8.GetBytes("hello tcp");
        await client.SendAsync(message);

        var received = await serverReceived.Task;
        Assert.Equal(message, received);

        // Echo should be received by client
        var clientEcho = await clientReceived.Task;
        Assert.Equal(message, clientEcho);

        client.Dispose();
        server.Dispose();
    }

    [Fact]
    public void Particle_ThrowsOnDisposed()
    {
        var server = new Reactor(new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        var endpoint = server.LocalEndPoint!;
        var client = new ParticleBuilder().ConnectTo(endpoint).Build();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.Send(new byte[] { 1, 2, 3 }));
        server.Dispose();
    }
}