using BenchmarkDotNet.Attributes;
using Faster.Transport.Ipc;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[MemoryDiagnoser]
[WarmupCount(3)]
[IterationCount(10)]
public class FasterIpcBenchmark
{
    private MappedReactor _server = null!;
    private MappedParticle _client = null!;

    private int _received;
    private TaskCompletionSource<bool> _tcs;

    private readonly byte[] _payload = Encoding.UTF8.GetBytes("hello world 1234567890");

    [GlobalSetup]
    public void Setup()
    {
        // Create the server
        const string Base = "FasterIpcDemo";
        var mre = new ManualResetEvent(false);
        var server = new MappedReactor(Base);
        server.OnConnected += id =>
        {
            mre.Set();
           // Console.WriteLine($"[SERVER] Client {id:X16} connected");
        };
        server.OnReceived += (particle, mem) =>
        {
           // Console.WriteLine($"[SERVER] <- {id:X16}: {msg}");
            particle.Send(mem.Span);
        };
        server.Start();

        _client = new MappedParticle(Base, 0xA1UL);

        _client.OnReceived = (p, payload) =>
        {
            Interlocked.Increment(ref _received);
            if (_received == 10_000)
            {
                _tcs.SetResult(true);
            }
        };

        _client.Start();

        mre.WaitOne(); // wait for connection   
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

    [Benchmark(Description = "IPC SendAsync 10k messages")]
    public async Task SendAsync_10K()
    {
        for (int i = 0; i < 10_000; i++)
            _client.Send(_payload);

        await _tcs.Task;
    }
}

