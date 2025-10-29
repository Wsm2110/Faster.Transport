using Faster.Transport;
using System.Net;
using System.Text;

// Multicast group
var group = IPAddress.Parse("239.10.10.10");
var port = 50000;

// 🛰️ Server (Sender)
var server = new ParticleBuilder()
    .UseMode(TransportMode.Udp)
    .WithMulticast(group, port, disableLoopback: false)
    .Build();

// 📡 Clients
var client1 = new ParticleBuilder()
    .UseMode(TransportMode.Udp)
    .WithMulticast(group, port, disableLoopback: false)
    .OnReceived((_, msg) =>
    {
        Console.WriteLine($"Client 1 got: {Encoding.UTF8.GetString(msg.Span)}");
    })
    .Build();

var client2 = new ParticleBuilder()
    .UseMode(TransportMode.Udp)
    .WithMulticast(group, port, disableLoopback: false)
    .OnReceived((_, msg) =>
    {
        Console.WriteLine($"Client 2 got: {Encoding.UTF8.GetString(msg.Span)}");
    })
    .Build();

Console.WriteLine("Press ENTER to send 3 multicast messages...");
Console.ReadLine();

for (int i = 0; i < 3; i++)
{
    var msg = Encoding.UTF8.GetBytes($"Broadcast #{i + 1}");
    await server.SendAsync(msg);
    Console.WriteLine($"[Server] Sent Broadcast #{i + 1}");
    await Task.Delay(300);
}
