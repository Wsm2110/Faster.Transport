using Faster.Transport;
using Faster.Transport.Contracts;
using System.Net;

// Create server
var server = new ParticleBuilder()
    .UseMode(TransportMode.Udp)
    .BindTo(new IPEndPoint(IPAddress.Any, 9000))
    .ConnectTo(new IPEndPoint(IPAddress.Loopback, 9001))
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"Server got: {System.Text.Encoding.UTF8.GetString(msg.Span)}");
        p.Send(msg.Span); // Echo back
    })
    .Build();

Console.WriteLine("UDP server listening on port 9000...");

// Create client
var client = new ParticleBuilder()
    .UseMode(TransportMode.Udp)
    .ConnectTo(new IPEndPoint(IPAddress.Loopback, 9000))
    .BindTo(new IPEndPoint(IPAddress.Any, 9001))
    .OnReceived((p, msg) =>
    {
        Console.WriteLine($"Client got echo: {System.Text.Encoding.UTF8.GetString(msg.Span)}");
    })
    .Build();

await client.SendAsync(System.Text.Encoding.UTF8.GetBytes("ping from UDP client!"));

Console.ReadLine();
