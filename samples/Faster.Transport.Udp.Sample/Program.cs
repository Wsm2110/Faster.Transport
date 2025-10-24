using Faster.Transport;
using Faster.Transport.Contracts;
using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;

await UdpDemo();

await UdpMulticastDemo();

static async Task UdpDemo()
{
    var port = 9600;
    var serverEp = new IPEndPoint(IPAddress.Loopback, port);
    var clientEp = new IPEndPoint(IPAddress.Loopback, port + 1);

    var server = new ParticleBuilder()
        .UseMode(TransportMode.Udp)
        .BindTo(serverEp)
        .ConnectTo(clientEp)
        .OnConnected(p => Console.WriteLine($"✅ Server listening on {serverEp}"))
        .OnReceived(async (p, msg) =>
        {
            var text = Encoding.UTF8.GetString(msg.Span);
            Console.WriteLine($"📨 [Server] Got: {text}");
            await p.SendAsync(msg); // echo
        })
        .Build();

    var client = new ParticleBuilder()
        .UseMode(TransportMode.Udp)
        .BindTo(clientEp)
        .ConnectTo(serverEp)
        .OnConnected(p => Console.WriteLine($"🚀 Client bound to {clientEp}"))
        .OnReceived((p, msg) =>
        {
            var text = Encoding.UTF8.GetString(msg.Span);
            Console.WriteLine($"📩 [Client] Echo reply: {text}");
        })
        .Build();

    while (true)
    {
        Console.Write("Enter message: ");
        var line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) continue;
        await client.SendAsync(Encoding.UTF8.GetBytes(line));
    }
}

static async Task UdpMulticastDemo()
{
    var group = IPAddress.Parse("239.0.0.123");
    var port = 9700;

    Console.WriteLine("=== UDP Multicast Demo ===");
    Console.WriteLine("Each peer will both send and receive multicast messages.");
    Console.WriteLine("Type a message and press ENTER to broadcast to the group.\n");

    // Create 2 simulated peers in the same process:
    var peer1 = CreatePeer("Peer1", new IPEndPoint(IPAddress.Any, port + 1), group, port);
    var peer2 = CreatePeer("Peer2", new IPEndPoint(IPAddress.Any, port + 2), group, port);

    // Interactive sender from Peer1
    while (true)
    {
        Console.Write("> ");
        var line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) continue;

        await peer1.SendAsync(Encoding.UTF8.GetBytes(line));
    }
}

static IParticle CreatePeer(string name, IPEndPoint localEp, IPAddress group, int port)
{
    var multicastEp = new IPEndPoint(group, port);

    var peer = new ParticleBuilder()
        .UseMode(TransportMode.Udp)
        .BindTo(localEp)
        .ConnectTo(multicastEp)
        .JoinMulticastGroup(group, disableLoopback: false) // disableLoopback = false so we see own messages
        .OnConnected(p => Console.WriteLine($"✅ {name} joined multicast group {group}:{port} (local {localEp})"))
        .OnReceived((p, msg) =>
        {
            var text = Encoding.UTF8.GetString(msg.Span);
            Console.WriteLine($"📩 {name} received: {text}");
        })
        .Build();

    return peer;
}