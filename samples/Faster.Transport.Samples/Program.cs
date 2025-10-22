using Faster.Transport;
using Faster.Transport.Contracts;
using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        ManualResetEvent manualResetEvent = new ManualResetEvent(false);

        // Start your Reactor server somewhere:
        var server = new Reactor(new IPEndPoint(IPAddress.Any, 5555));
        server.OnReceived = (client, payload) => client.Return(payload);
        server.OnConnected = (client) =>
        {
            manualResetEvent.Set();
        };
        server.Start();

        // Build a single-threaded async Particle
        IParticle particle = new ParticleBuilder()
            .ConnectTo(new IPEndPoint(IPAddress.Parse("192.168.2.8"), 5555))
            .OnReceived(data =>
            {
                string message = Encoding.UTF8.GetString(data.Span);
                Console.WriteLine($"[Particle] Received: {message}");
            })
            .OnParticleDisconnected((cli, ex) =>
            {
                Console.WriteLine($"[Particle] Disconnected: {ex?.Message}");
            })
            .Build();

        manualResetEvent.WaitOne();

        // Send and await response
        string text = "Hello Reactor!";
        await particle.SendAsync(Encoding.UTF8.GetBytes(text));

        Console.ReadLine();

        //              Burst Example
        //--------------------------------------------

        // Build a ParticleBurst client (fire-and-forget)
        IParticleBurst burst = new ParticleBuilder()
            .ConnectTo(new IPEndPoint(IPAddress.Loopback, 5555))
            .AsBurst()
             .OnReceived(data =>
            {
                string message = Encoding.UTF8.GetString(data.Span);
                Console.WriteLine($"[ParticleBurst] Echo: {message}");
            })
            .OnBurstDisconnected((cli, ex) =>
            {
                Console.WriteLine($"[ParticleBurst] Disconnected: {ex?.Message}");
            })
            .BuildBurst();

        // wait until connected
        manualResetEvent.WaitOne();

        // Prepare payload
        var payload = Encoding.UTF8.GetBytes("Hello Reactor ⚡");
        burst.Send(payload);

        await Task.Delay(1000);

        Console.ReadLine();
    }
}
