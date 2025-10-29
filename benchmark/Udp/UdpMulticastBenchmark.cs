using BenchmarkDotNet.Attributes;
using Faster.Transport;
using Faster.Transport.Contracts;
using Faster.Transport.Features.Tcp;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

[MemoryDiagnoser]
[GcServer(true)]
[WarmupCount(5)]
[IterationCount(50)]
public class UdpMulticastBenchmark
{
    private IParticle _server;
    private IParticle _client;

    private ReadOnlyMemory<byte> _payload = null!;
    private int _messageCount;
    private int _received;
    private TaskCompletionSource<bool> _tcs;

    [Params(10_000)]
    public int MessageCount { get; set; }

    [Params(20)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _messageCount = MessageCount;
        _payload = Enumerable.Repeat((byte)42, PayloadSize).ToArray();

        // Multicast group
        var group = IPAddress.Parse("239.10.10.10");
        var port = 50000;

        var mre = new ManualResetEvent(false);

        // 🛰️ Server (Sender)
        _server = new ParticleBuilder()
            .UseMode(TransportMode.Udp)          
            .WithMulticast(group, port, disableLoopback: false)
            .OnReceived((p, data) => p.Send(_payload.Span)) // echo back)
            .OnConnected(p => mre.Set())
            .Build();

        // 📡 Clients
        _client = new ParticleBuilder()          
            .UseMode(TransportMode.Udp)
            .WithMulticast(group, port, disableLoopback: false)
            .OnReceived((_, msg) =>
            {
                if (Interlocked.Increment(ref _received) == _messageCount)
                    _tcs.TrySetResult(true);
            })
            .Build();

        //  mre.WaitOne();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client?.Dispose();
        _server?.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        Interlocked.Exchange(ref _received, 0);
        _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [Benchmark(Description = "Roundtrip throughput (async)")]
    public async Task RoundTripThroughput()
    {
        // Parallelized send
        for (int i = 0; i < MessageCount; i++)
        {
            await _client.SendAsync(_payload);
        }
    }
}
