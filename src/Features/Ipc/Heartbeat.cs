using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace Faster.Transport.Ipc
{
    internal sealed class HeartbeatWriter : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly Thread _thread;
        private volatile bool _running;

        public HeartbeatWriter(string name)
        {
            _mmf = MmfHelper.CreateWithSecurity(name, 8);
            _view = _mmf.CreateViewAccessor(0, 8, MemoryMappedFileAccess.ReadWrite);
            _thread = new Thread(Loop) { IsBackground = true, Name = $"hb-writer:{name}" };
        }

        public void Start() { _running = true; _thread.Start(); }

        private void Loop()
        {
            while (_running)
            {
                long ticks = DateTime.UtcNow.Ticks;
                _view.Write(0, ticks);
                Thread.Sleep(100);
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _thread.Join(250); } catch { }
            _view.Dispose();
            _mmf.Dispose();
        }
    }

    internal static class HeartbeatReader
    {
        public static bool TryRead(string name, out long ticks)
        {
            ticks = 0;
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                using var view = mmf.CreateViewAccessor(0, 8, MemoryMappedFileAccess.Read);
                view.Read(0, out ticks);
                return true;
            }
            catch { return false; }
        }
    }
}
