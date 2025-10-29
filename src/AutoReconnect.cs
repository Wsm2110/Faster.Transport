using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Faster.Transport
{
    /// <summary>
    /// Automatically reconnects to a transport endpoint when disconnected.
    /// Stops retrying while connected, resumes only on disconnection.
    /// Works across all <see cref="IParticle"/> transport modes.
    /// </summary>
    internal sealed class AutoReconnectWrapper : IParticle
    {
        private readonly Func<IParticle> _factory;
        private readonly TimeSpan _baseDelay;
        private readonly TimeSpan _maxDelay;
        private readonly Action<IParticle>? _onConnected;
        private readonly Action<IParticle>? _onDisconnected;
        private readonly Action<IParticle, ReadOnlyMemory<byte>>? _onReceived;

        private IParticle? _current;
        private volatile bool _running;
        private int _attempt;
        private readonly CancellationTokenSource _cts = new();

        public AutoReconnectWrapper(
            Func<IParticle> factory,
            TimeSpan baseDelay,
            TimeSpan maxDelay,
            Action<IParticle>? onConnected,
            Action<IParticle>? onDisconnected,
            Action<IParticle, ReadOnlyMemory<byte>>? onReceived)
        {
            _factory = factory;
            _baseDelay = baseDelay;
            _maxDelay = maxDelay;
            _onConnected = onConnected;
            _onDisconnected = onDisconnected;
            _onReceived = onReceived;

            Start();
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _ = Task.Run(ReconnectLoop, _cts.Token);
        }

        private async Task ReconnectLoop()
        {
            while (_running && !_cts.IsCancellationRequested)
            {
                try
                {
                    _attempt++;

                    // Try to create and connect a new particle
                    var p = _factory();
                    _current = p;

                    // Wire events
                    if (_onReceived != null)
                        p.OnReceived = _onReceived;

                    p.OnDisconnected = _ =>
                    {
                        Console.WriteLine("[AutoReconnect] Disconnected. Scheduling reconnect...");
                        _onDisconnected?.Invoke(this);

                        // Resume reconnect loop
                        if (_running && !_cts.IsCancellationRequested)
                             Task.Run(ReconnectLoop, _cts.Token);
                    };

                    _onConnected?.Invoke(p);
                    _attempt = 0;

                    // ✅ Connected successfully — exit the reconnect loop
                    return;
                }
                catch (SocketException)
                {
                    var delay = ComputeDelay();
                    Console.WriteLine($"[AutoReconnect] Connection failed. Retry in {delay.TotalSeconds:F1}s...");
                    await Task.Delay(delay, _cts.Token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AutoReconnect] Error: {ex.Message}");
                    await Task.Delay(_maxDelay, _cts.Token);
                }
            }
        }

        private TimeSpan ComputeDelay()
        {
            double ms = Math.Min(_baseDelay.TotalMilliseconds * Math.Pow(2, _attempt), _maxDelay.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(ms);
        }

        // -----------------------------------------------------
        // IParticle Forwarding
        // -----------------------------------------------------
        public void Send(ReadOnlySpan<byte> data) => _current?.Send(data);

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload) =>
            _current != null ? _current.SendAsync(payload) : TaskCompat.CompletedValueTask;

        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived
        {
            get => _current?.OnReceived;
            set { if (_current != null) _current.OnReceived = value; }
        }

        public Action<IParticle>? OnConnected
        {
            get => _current?.OnConnected;
            set { if (_current != null) _current.OnConnected = value; }
        }

        public Action<IParticle>? OnDisconnected
        {
            get => _current?.OnDisconnected;
            set { if (_current != null) _current.OnDisconnected = value; }
        }

        public void Dispose()
        {
            _running = false;
            _cts.Cancel();
            try { _current?.Dispose(); } catch { }
            _cts.Dispose();
        }
    }
}
