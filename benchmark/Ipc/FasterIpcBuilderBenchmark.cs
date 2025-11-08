using BenchmarkDotNet.Attributes;
using Faster.Transport.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Faster.Transport.Benchmarks
{
    [MemoryDiagnoser]
    [ThreadingDiagnoser]
    public class FasterIpcBuilderBenchmark : IDisposable
    {
        private IParticle _server = null!;
        private IParticle _client = null!;
        private int _received;
        private string _channel = null!;
        private byte[] _payload = null!;
        private TaskCompletionSource<bool> _tcs;
        private readonly AutoResetEvent _ack = new(false);


        [Params(20)]
        public int PayloadBytes { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _channel = $"FasterIpc_{Guid.NewGuid():N}";
            _payload = new byte[PayloadBytes];
            new Random(42).NextBytes(_payload);

            _server = new ParticleBuilder()
                .UseMode(TransportMode.Ipc)              
                .OnReceived((p, data) =>
                {
                    p.Send(data.Span);
                })
                .Build();

            _client = new ParticleBuilder()
                .UseMode(TransportMode.Ipc)
                .WithChannel(_channel)
                .OnReceived((p, data) =>
                {
                    _tcs.SetResult(true);
                })
                .Build();


            // Warm up connection
            Thread.Sleep(200);
        }

        [IterationSetup]
        public void IterationSetup()
        {
            Interlocked.Exchange(ref _received, 0);
        }

        [Benchmark(Description = "IPC SendAsync 10k messages")]
        public async Task SendAsync_10K()
        {
            for (int i = 0; i < 10_000; i++)
            {
                _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _client.Send(_payload);
                await _tcs.Task;
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _client.Dispose();
            _server.Dispose();
        }

        public void Dispose() => Cleanup();

    }
}
