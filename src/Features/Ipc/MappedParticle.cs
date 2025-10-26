using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace Faster.Transport.Ipc
{
    public sealed class MappedParticle : MappedParticleBase
    {
        private readonly ulong _clientId;
        private readonly string _ns;
        private readonly string _base;

        public event Action? OnServerDisconnected;

        public MappedParticle(string baseName, ulong clientId, bool global = false, int ringBytes = (128 + (1 << 20)))
            : base(
                rx: new DirectionalChannel($"{(global ? "Global" : "Local")}\\{baseName}.S2C.{clientId:X16}.map",
                                           evName: null, totalBytes: ringBytes, create: true, isReader: true, rxThreadName: $"cli-rx:{clientId:X16}", useEvent: false),
                tx: new DirectionalChannel($"{(global ? "Global" : "Local")}\\{baseName}.C2S.{clientId:X16}.map",
                                           evName: null, totalBytes: ringBytes, create: true, isReader: false, rxThreadName: null, useEvent: false))
        {
            _clientId = clientId;
            _ns = global ? "Global\\" : "Local\\";
            _base = baseName;
        }

        public override void Start()
        {
            base.Start();
            Register();
        }

        private void Register()
        {
            string regMap = _ns + $"{_base}.Registry.map";
            string regMtx = _ns + $"{_base}.Registry.mtx";
            using var reg = MmfHelper.CreateWithSecurity(regMap, 64 * 1024);
            using var view = reg.CreateViewAccessor(0, 64 * 1024, MemoryMappedFileAccess.ReadWrite);
            using var mutex = new Mutex(false, regMtx);

            mutex.WaitOne();
            try
            {
                var buf = new byte[64 * 1024];
                view.ReadArray(0, buf, 0, buf.Length);
                int len = Array.IndexOf(buf, (byte)0);
                if (len < 0) len = buf.Length;
                var add = Encoding.ASCII.GetBytes(_clientId.ToString("X16") + "\n");
                if (len + add.Length <= buf.Length)
                {
                    Array.Copy(add, 0, buf, len, add.Length);
                    view.WriteArray(0, buf, 0, buf.Length);
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
}
