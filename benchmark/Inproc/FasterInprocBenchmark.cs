using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
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
    private InprocReactor _hub = null!;
    private InprocParticle _client = null!;
    private int _received;
    private TaskCompletionSource<bool> _tcs;
    private readonly byte[] _payload = Encoding.UTF8.GetBytes("hello world 1234567890");

    [GlobalSetup]
    public void Setup()
    {
        _hub = new InprocReactor("bench", bufferSize: 8192, ringCapacity: 8192);
        _hub.OnReceived = (particle, data) =>
          {
              // echo back
              particle.Send(data.Span);
          };

        _hub.Start();

        _client = new InprocParticle("bench", isServer: false);
        _client.OnReceived = (_, data) =>
        {
            Interlocked.Increment(ref _received);
            if (_received == 10000)
            {
                _tcs.SetResult(true);
            }
        };

        _client.Start();

        Task.Delay(100).Wait(); // wait for setup
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hub.Dispose();
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
