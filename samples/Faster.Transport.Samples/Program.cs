using Faster.Transport;
using Faster.Transport.Features.Tcp;
using System.Net;

class Program
{
    static async Task Main()
    {
        ManualResetEvent manualResetEvent = new ManualResetEvent(false);

        // Start your Reactor server somewhere:
        var server = new Reactor(new IPEndPoint(IPAddress.Any, 5555));
        server.OnReceived = (client, payload) => client.Send(payload.Span);
        server.OnConnected = (client) =>
        {
            manualResetEvent.Set();
        };
        server.Start();

        var client = new ParticleBuilder()
            .WithRemote(new IPEndPoint(IPAddress.Loopback, 5555))
            .WithBufferSize(16384)
            .WithParallelism(8)
            .OnReceived((_, data) => Console.WriteLine($"Received {data.Length} bytes"))    
            .Build();

        manualResetEvent.WaitOne();

        // Send asynchronously
        await client.SendAsync(new byte[] { 0x01, 0x02, 0x03 });

        // Send synchronously
        client.Send(new byte[] { 0x10, 0x20, 0x30 });

        Console.ReadLine();
    }
}
