using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Faster.Transport.Ipc
{
    /// <summary>
    /// Provides lightweight, high-performance tracing and diagnostic logging
    /// for the Faster.Transport IPC layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This static utility writes timestamped messages to the console (or to a custom sink)
    /// with minimal allocations and overhead.
    /// </para>
    /// <para>
    /// You can enable or disable all logging globally by toggling <see cref="Enabled"/>,
    /// or redirect output to your own logger by setting <see cref="CustomSink"/>.
    /// </para>
    /// <example>
    /// Example:
    /// <code>
    /// FasterIpcTrace.Enabled = true;
    /// FasterIpcTrace.Info("Server started");
    /// FasterIpcTrace.Warn("Client disconnected unexpectedly");
    /// FasterIpcTrace.Error("Failed to open IPC channel", ex);
    /// </code>
    /// </example>
    /// </remarks>
    public static class FasterIpcTrace
    {
        /// <summary>
        /// Enables or disables logging globally.
        /// </summary>
        /// <remarks>
        /// When <see langword="false"/>, all calls to <see cref="Info"/>, <see cref="Warn"/>,
        /// and <see cref="Error"/> are ignored.
        /// </remarks>
        public static bool Enabled = true;

        /// <summary>
        /// Optional custom output handler for log messages.
        /// </summary>
        /// <remarks>
        /// If set, all messages are sent to this delegate instead of <see cref="Console.WriteLine"/>.
        /// Useful for integrating into structured logging frameworks (e.g., Serilog, NLog).
        /// </remarks>
        public static Action<string>? CustomSink;

        /// <summary>
        /// Writes an informational message with a timestamp.
        /// </summary>
        /// <param name="msg">The text message to log.</param>
        /// <remarks>
        /// Uses <see cref="string.Create"/> to efficiently format output with zero intermediate allocations.
        /// Example output:
        /// <code>
        /// 12:34:56.789 | Client 0xA1 connected
        /// </code>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Info(string msg)
        {
            if (!Enabled) return;

            var now = DateTime.UtcNow;

            // Create a timestamped log line efficiently using string.Create
            var line = string.Create(64 + msg.Length, (now, msg), static (span, data) =>
            {
                var (time, text) = data;
                _ = time.TryFormat(span, out int written, "HH:mm:ss.fff");
                span[written++] = ' ';
                span[written++] = '|';
                span[written++] = ' ';
                text.AsSpan().CopyTo(span[written..]);
            });

            // Output to the user-provided sink if set, otherwise to the console
            if (CustomSink != null)
                CustomSink(line);
            else
                Console.WriteLine(line);
        }

        /// <summary>
        /// Writes a warning message.
        /// </summary>
        /// <param name="msg">The warning text to log.</param>
        /// <remarks>
        /// Automatically prefixes the message with <c>[WARN]</c>.
        /// </remarks>
        public static void Warn(string msg)
        {
            if (Enabled)
                Info("[WARN] " + msg);
        }

        /// <summary>
        /// Writes an error message, optionally including an exception message.
        /// </summary>
        /// <param name="msg">The error description.</param>
        /// <param name="ex">Optional exception to include in the output.</param>
        /// <remarks>
        /// <para>
        /// If an exception is provided, its message is appended in parentheses,
        /// and the full stack trace is written to <see cref="Debug.WriteLine"/>.
        /// </para>
        /// Example:
        /// <code>
        /// [ERR] Failed to attach client (File not found)
        /// </code>
        /// </remarks>
        public static void Error(string msg, Exception? ex = null)
        {
            if (!Enabled) return;

            Info("[ERR] " + msg + (ex != null ? $" ({ex.Message})" : ""));

            // Write full stack trace to Debug output for deeper inspection
            if (ex != null)
                Debug.WriteLine(ex);
        }
    }
}
