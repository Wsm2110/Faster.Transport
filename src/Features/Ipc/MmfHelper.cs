using System;
using System.ComponentModel;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Faster.Transport.Ipc
{
    /// <summary>
    /// Provides helper methods for working with <see cref="MemoryMappedFile"/> and <see cref="EventWaitHandle"/> 
    /// used by the IPC (Inter-Process Communication) transport layer.
    /// </summary>
    /// <remarks>
    /// This class is responsible for safely creating or opening shared memory regions and synchronization 
    /// events between processes. 
    /// It uses retry logic to handle race conditions when multiple clients or the server 
    /// try to create or open the same shared objects at startup.
    /// </remarks>
    internal static class MmfHelper
    {

        // Fix 4: If you need security on Windows with .NET 5+, use P/Invoke

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(
            IntPtr hFile,
            IntPtr lpFileMappingAttributes,
            uint flProtect,
            uint dwMaximumSizeHigh,
            uint dwMaximumSizeLow,
            string lpName);

        /// <summary>
        /// Creates or opens a memory-mapped file (MMF) with read/write access and proper sharing configuration.
        /// </summary>
        /// <param name="name">The unique name of the shared memory region (e.g., "Local\\FasterIpc.MyMap").</param>
        /// <param name="size">The total size of the memory-mapped file, in bytes.</param>
        /// <returns>
        /// A <see cref="MemoryMappedFile"/> that can be used to read and write shared data between processes.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The <paramref name="name"/> parameter identifies the MMF globally or locally:
        /// <list type="bullet">
        /// <item><term>Local\\</term> — visible only to processes of the same user session.</item>
        /// <item><term>Global\\</term> — visible system-wide (requires admin privileges).</item>
        /// </list>
        /// </para>
        /// </remarks>
        public static MemoryMappedFile CreateWithSecurity(string name, long capacity)
        {
            const uint PAGE_READWRITE = 0x04;
            const uint SEC_COMMIT = 0x8000000;

            IntPtr handle = CreateFileMapping(
                new IntPtr(-1),  // INVALID_HANDLE_VALUE
                IntPtr.Zero,     // default security
                PAGE_READWRITE | SEC_COMMIT,
                (uint)(capacity >> 32),
                (uint)(capacity & 0xFFFFFFFF),
                name
            );

            if (handle == IntPtr.Zero)
                throw new Win32Exception();

            return MemoryMappedFile.CreateOrOpen(name, capacity, MemoryMappedFileAccess.ReadWrite);
        }
          

        /// <summary>
        /// Tries to open an existing memory-mapped file (MMF), retrying several times if it's not yet available.
        /// </summary>
        /// <param name="name">The name of the memory-mapped file to open.</param>
        /// <param name="tries">Number of retry attempts (default: 200).</param>
        /// <param name="delayMs">Delay between retries, in milliseconds (default: 5).</param>
        /// <returns>
        /// A <see cref="MemoryMappedFile"/> instance if successfully opened.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the MMF could not be opened after all retry attempts.
        /// </exception>
        /// <remarks>
        /// This method is useful when the client starts slightly before the server has finished 
        /// creating the shared memory region.
        /// </remarks>
        public static MemoryMappedFile OpenExistingWithRetry(string name, int tries = 100, int delayMs = 5)
        {
            for (int i = 0; i < tries; i++)
            {
                try
                {
                    // Try opening the existing shared memory region
                    return MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
                }
                catch (FileNotFoundException)
                {
                    // If not yet created, wait a bit and try again
                    Thread.Sleep(100);
                }
            }

            throw new InvalidOperationException($"Cannot open MMF '{name}' after {tries} retries.");
        }

        /// <summary>
        /// Creates or opens an <see cref="EventWaitHandle"/> for process-to-process signaling.
        /// </summary>
        /// <param name="name">The global or local event name (e.g., "Local\\FasterIpc.MyEvent").</param>
        /// <returns>
        /// A <see cref="EventWaitHandle"/> that can be used to signal or wait for synchronization events.
        /// </returns>
        /// <remarks>
        /// Auto-reset mode means that the event automatically returns to a non-signaled state after one waiting thread is released.
        /// </remarks>
        public static EventWaitHandle CreateOrOpenEvent(string name)
        {
            // Creates the event if it doesn't exist; otherwise attaches to the existing one.
            return new EventWaitHandle(false, EventResetMode.AutoReset, name);
        }

        /// <summary>
        /// Tries to open an existing <see cref="EventWaitHandle"/>, retrying if it's not yet created.
        /// </summary>
        /// <param name="name">The name of the event to open.</param>
        /// <param name="tries">Number of retry attempts (default: 200).</param>
        /// <param name="delayMs">Delay between retries, in milliseconds (default: 5).</param>
        /// <returns>
        /// A <see cref="EventWaitHandle"/> instance if successfully opened.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the event could not be opened after all retry attempts.
        /// </exception>
        /// <remarks>
        /// This method ensures reliable synchronization between server and client processes 
        /// even when one side starts before the other.
        /// </remarks>
        public static EventWaitHandle OpenEventWithRetry(string name, int tries = 200, int delayMs = 5)
        {
            for (int i = 0; i < tries; i++)
            {
                try
                {
                    // Attempt to open an existing event
                    return EventWaitHandle.OpenExisting(name);
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // The event is not yet created by the other process, wait and retry
                    Thread.Sleep(delayMs);
                }
            }

            throw new InvalidOperationException($"Cannot open Event '{name}' after {tries} retries.");
        }
    }
}
