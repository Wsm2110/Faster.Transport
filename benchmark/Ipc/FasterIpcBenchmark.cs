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
    private MappedParticle _server = null!;
    private MappedParticle _client = null!;

    private int _received;
    private TaskCompletionSource<bool> _tcs;

    private readonly byte[] _payload = Encoding.UTF8.GetBytes("hello world 1234567890");

    [GlobalSetup]
    public void Setup()
    {
        // Create the server
        _server = new MappedParticle("MyChannel", true);      

        _server.OnReceived = (client, payload) =>
        {
            client.Send(payload.Span);
        };
        
        Console.WriteLine("Server ready. Waiting for client...");

        // give server a bit of time to start
        Task.Delay(200).Wait();

        // Connect to same channel
        _client = new MappedParticle("MyChannel", isServer: false);

        _client.OnReceived = (particle, payload) =>
        {
            Interlocked.Increment(ref _received);
            if (_received == 10000)
            {
                _tcs.SetResult(true);
            }
        };

        Console.WriteLine("Client connected. Type messages:");

        Task.Delay(200).Wait(); // ensure connected
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

 