using System;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Text;
using Faster.Transport.Contracts;

namespace Faster.Transport.Ipc
{
    public sealed class MappedReactor : IDisposable
    {
        private readonly string _ns;
        private readonly string _base;
        private readonly int _ringBytes;
        private readonly ConcurrentDictionary<ulong, ClientParticle> _clients = new();
        private readonly Thread _registryThread;
        private volatile bool _running;

        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }
        public Action<ulong>? OnConnected{ get; set; }
        public Action<ulong>? OnClientDisconnected { get; set; }

        public MappedReactor(string baseName, bool global = false, int ringBytes = (128 + (1 << 20)))
        {
            _ns = global ? "Global\\" : "Local\\";
            _base = baseName;
            _ringBytes = ringBytes;
            _registryThread = new Thread(RegistryLoop) { IsBackground = true, Name = "ipc-registry" };
        }

        public void Start()
        {
            _running = true;
            _registryThread.Start();
        }

        private void RegistryLoop()
        {
            string regMap = _ns + $"{_base}.Registry.map";
            string regMtx = _ns + $"{_base}.Registry.mtx";
            using var reg = MmfHelper.CreateWithSecurity(regMap, 64 * 1024);
            using var view = reg.CreateViewAccessor(0, 64 * 1024, MemoryMappedFileAccess.ReadWrite);
            using var mutex = new Mutex(false, regMtx);

            var buf = new byte[64 * 1024];
            var seen = new HashSet<ulong>();

            while (_running)
            {
                mutex.WaitOne();
                try { view.ReadArray(0, buf, 0, buf.Length); }
                finally { mutex.ReleaseMutex(); }

                int len = Array.IndexOf(buf, (byte)0);
                if (len < 0) len = buf.Length;

                var text = Encoding.ASCII.GetString(buf, 0, len);
                foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!ulong.TryParse(line, System.Globalization.NumberStyles.HexNumber, null, out var id))
                        continue;
                    if (seen.Add(id))
                        TryAttachClient(id);
                }

                Thread.Sleep(50);
            }
        }

        private void TryAttachClient(ulong id)
        {
            try
            {
                string c2sMap = _ns + $"{_base}.C2S.{id:X16}.map";
                string s2cMap = _ns + $"{_base}.S2C.{id:X16}.map";

                var rx = new DirectionalChannel(c2sMap, evName: null, totalBytes: _ringBytes, create: false, isReader: true, rxThreadName: $"srv-rx:{id:X16}", useEvent: false);
                var tx = new DirectionalChannel(s2cMap, evName: null, totalBytes: _ringBytes, create: false, isReader: false, rxThreadName: null, useEvent: false);

                var particle = new ClientParticle(rx, tx, id, this);
                _clients[id] = particle;
                particle.Start();
                FasterIpcTrace.Info($"[Server] Client {id:X16} connected");
                OnConnected?.Invoke(id);
            }
            catch (Exception ex)
            {
                FasterIpcTrace.Warn($"[Server] Attach failed for {id:X16}: {ex.Message}");
            }
        }

        public void Send(ulong id, ReadOnlySpan<byte> payload)
        {
            if (_clients.TryGetValue(id, out var p))
                p.Send(payload);
        }

        public void Broadcast(ReadOnlySpan<byte> payload)
        {
            foreach (var kv in _clients)
            {
                try { kv.Value.Send(payload); } catch { }
            }
        }

        private sealed class ClientParticle : MappedParticleBase
        {
            private readonly ulong _id;
            private readonly MappedReactor _owner;

            public ClientParticle(DirectionalChannel rx, DirectionalChannel tx, ulong id, MappedReactor owner)
                : base(rx, tx)
            {
                _id = id; _owner = owner;
                OnReceived += (self, data) => _owner.OnReceived?.Invoke(self, data);
            }

            public override void Dispose()
            {
                base.Dispose();
                _owner.OnClientDisconnected?.Invoke(_id);
            }
        }

        public void Dispose()
        {
            _running = false;
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
        }
    }
}
