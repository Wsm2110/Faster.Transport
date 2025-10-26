using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Faster.Transport.Ipc
{
    public static class FasterIpcTrace
    {
        public static bool Enabled = true;
        public static Action<string>? CustomSink;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Info(string msg)
        {
            if (!Enabled) return;
            var now = DateTime.UtcNow;
            var line = string.Create(64 + msg.Length, (now, msg), static (span, data) =>
            {
                var (time, text) = data;
                _ = time.TryFormat(span, out int written, "HH:mm:ss.fff");
                span[written++] = ' ';
                span[written++] = '|';
                span[written++] = ' ';
                text.AsSpan().CopyTo(span[written..]);
            });
            if (CustomSink != null) CustomSink(line); else Console.WriteLine(line);
        }

        public static void Warn(string msg) { if (Enabled) Info("[WARN] " + msg); }
        public static void Error(string msg, Exception? ex = null)
        {
            if (!Enabled) return;
            Info("[ERR] " + msg + (ex != null ? $" ({ex.Message})" : ""));
            if (ex != null) Debug.WriteLine(ex);
        }
    }
}
