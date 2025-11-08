using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Faster.Transport;
using Faster.Transport.Contracts;
using Faster.Transport.Inproc;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[MemoryDiagnoser]
[WarmupCount(3)]
[IterationCount(10)]
public class FasterInprocBenchmark
{
    private IReactor _server = null!;
    private IParticle _client = null!;
    private int _received;
    private TaskCompletionSource<bool> _tcs;
    private readonly byte[] _payload = Encoding.UTF8.GetBytes("hello world 1234567890");

    [GlobalSetup]
    public void Setup()
    {
        _server = new ReactorBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel("bench")
            .OnReceived((particle, data) =>
            {
                particle.Send(data.Span);
            })
            .Build();

        _client = new ParticleBuilder()
            .UseMode(TransportMode.Inproc)
            .WithChannel("bench")
            .OnReceived((_, data) =>
            {
                Interlocked.Increment(ref _received);
                if (_received == 10000)
                {
                    _tcs.SetResult(true);
                }
            }).Build();

        Task.Delay(100).Wait(); // wait for setup
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _server.Dispose();
        _client.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        Interlocked.Exchange(ref _received, 0);
        _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [Benchmark(Description = "SendAsync 10k messages")]
    public async Task SendAsync_10K()
    {
        for (int i = 0; i < 10_000; i++)
        {
            _client.Send(_payload);
        }

        await _tcs.Task;
    }
}