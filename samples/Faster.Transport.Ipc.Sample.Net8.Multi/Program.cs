using Faster.Transport;
using System;
using System.Text;

var server = new ParticleBuilder()
    .UseMode(TransportMode.Ipc)
    .WithChannel("FasterIpcDemo", isServer: true)
    .OnConnected(p => Console.WriteLine("Client connected"))
    .OnReceived((p, data) =>
    {
        Console.WriteLine($"[Server] Received: {Encoding.UTF8.GetString(data.Span)}");
        p.Send(Encoding.UTF8.GetBytes("Echo: " + Encoding.UTF8.GetString(data.Span)));
    })
    .Build();

var client = new ParticleBuilder()
    .UseMode(TransportMode.Ipc)
    .WithChannel("FasterIpcDemo")
    .OnReceived((p, data) =>
        Console.WriteLine($"[Client] Got reply: {Encoding.UTF8.GetString(data.Span)}"))
    .Build();

client.Send(Encoding.UTF8.GetBytes("hello world"));

Console.ReadLine();