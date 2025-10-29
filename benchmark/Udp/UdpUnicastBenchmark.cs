using BenchmarkDotNet.Attributes;
using Faster.Transport;
using Faster.Transport.Contracts;
using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[MemoryDiagnoser]
[GcServer(true)]
[WarmupCount(5)]
[IterationCount(50)]
public class UdpUnicastEchoBenchmark
{
    private IParticle _server;
    private IParticle _client;

    private ReadOnlyMemory<byte> _payload = null!;
    private int _received;
    private TaskCompletionSource<bool> _tcs;

    [Params(20_000)]
    public int MessageCount { get; set; }

    [Params(20)]
    public int PayloadSize { get; set; }

    private readonly IPEndPoint _serverEndpoint = new(IPAddress.Loopback, 9000);
    private readonly IPEndPoint _clientEndpoint = new(IPAddress.Loopback, 9001);

    [GlobalSetup]
    public void Setup()
    {
        _payload = Enumerable.Repeat((byte)42, PayloadSize).ToArray();

        // 🛰️ Server
        _server = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .BindTo(new IPEndPoint(IPAddress.Any, _serverEndpoint.Port))
            .ConnectTo(_clientEndpoint)
            .OnReceived((p, msg) =>
            {
                // Echo back to client
                p.Send(msg.Span);
            })
            .Build();

        // 📡 Client
        _client = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .BindTo(new IPEndPoint(IPAddress.Any, _clientEndpoint.Port))
            .ConnectTo(_serverEndpoint)
            .OnReceived((p, msg) =>
            {
                if (Interlocked.Increment(ref _received) == MessageCount)
                    _tcs.TrySetResult(true);
            })
            .Build();

        // Let sockets bind properly
        Thread.Sleep(500);
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

    [Benchmark(Description = "UDP Unicast Roundtrip Throughput")]
    public async Task RoundtripThroughputAsync()
    {
        // Send many messages and wait for echo responses
        for (int i = 0; i < MessageCount; i++)
        {
            await _client.SendAsync(_payload);
        }

        await _tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
