using BenchmarkDotNet.Attributes;
using Faster.Transport.Contracts;
using Faster.Transport.Features.Tcp;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Faster.Transport.Benchmarks
{
    [MemoryDiagnoser]
    [GcServer(true)]
    [WarmupCount(5)]
    [IterationCount(50)]
    public class TcpSendBenchmark
    {
        private IParticle _server;
        private IParticle _client;

        private ReadOnlyMemory<byte> _payload = null!;
        private TaskCompletionSource<bool> _tcs = null!;
        private int _messageCount;

        [Params(10_000)]
        public int MessageCount { get; set; }

        [Params(20)]
        public int PayloadSize { get; set; }

        private static byte[] Bytes(int size)
        {
            var arr = new byte[size];
            Array.Fill(arr, (byte)42);
            return arr;
        }

        [GlobalSetup]
        public void Setup()
        {
            _messageCount = MessageCount;
            _payload = Bytes(PayloadSize);

            var channel = $"TcpBench_{Guid.NewGuid():N}";
            var endpoint = new IPEndPoint(IPAddress.Loopback, 5555);

            // --- Server ---
           // _server = new Reactor();

            // --- Client ---
            _client = new ParticleBuilder()
                .UseMode(TransportMode.Tcp)
                .WithRemote(endpoint)
                .OnReceived((_, data) =>
                {
                    _tcs?.TrySetResult(true);
                })
                .Build();

            // Wait for connection establishment
            Thread.Sleep(200);
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
            // Reset per-iteration state
            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        [Benchmark(Description = "TCP Roundtrip Throughput (async)")]
        public async Task RoundTripThroughput()
        {
            for (int i = 0; i < _messageCount; i++)
            {
                _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _client.Send(_payload.Span);
                await _tcs.Task.ConfigureAwait(false);
            }
        }
    }
}
