using BenchmarkDotNet.Attributes;
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
public class FasterConcurrentBenchmark
{
    private Reactor _server;
    private Particle _client;

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

        var endpoint = new IPEndPoint(IPAddress.Loopback, 5555);

        _server = new Reactor(endpoint, 64);
        _server.OnReceived = (conn, payload) =>
        {
            conn.Send(_payload.Span); // echo back
        };

        _server.Start();

        // Allow listener to initialize
        Thread.Sleep(100);

        _client = new Particle(endpoint, maxDegreeOfParallelism: 64);
        _client.OnReceived = (_, payload) =>
        {
            if (Interlocked.Increment(ref _received) == MessageCount)
                _tcs.TrySetResult(true);
        };
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
        int length = 2;
        int chunk = _messageCount / length;
        var tasks = new Task[length];

        for (int t = 0; t < tasks.Length; t++)
        {
            tasks[t] = Task.Run(async () =>
            {
                for (int i = 0; i < chunk; i++)
                {
                    await _client.SendAsync(_payload);
                }
            });
        }

        await Task.WhenAll(tasks);
        await _tcs.Task; // wait for all responses
    }
}
