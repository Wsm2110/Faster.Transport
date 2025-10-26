using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Faster.Transport.Ipc
{
    internal static class MmfHelper
    {
        public static MemoryMappedFile CreateWithSecurity(string name, long size)
        {            
            return MemoryMappedFile.CreateOrOpen(
                name, size, MemoryMappedFileAccess.ReadWrite,
                MemoryMappedFileOptions.None, HandleInheritability.None);
        }

        public static MemoryMappedFile OpenExistingWithRetry(string name, int tries = 200, int delayMs = 5)
        {
            for (int i = 0; i < tries; i++)
            {
                try { return MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite); }
                catch (FileNotFoundException) { Thread.Sleep(delayMs); }
            }
            throw new InvalidOperationException($"Cannot open MMF '{name}' after retries.");
        }

        public static EventWaitHandle CreateOrOpenEvent(string name)
        {           
            return new EventWaitHandle(false, EventResetMode.AutoReset, name);
        }

        public static EventWaitHandle OpenEventWithRetry(string name, int tries = 200, int delayMs = 5)
        {
            for (int i = 0; i < tries; i++)
            {
                try { return EventWaitHandle.OpenExisting(name); }
                catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(delayMs); }
            }
            throw new InvalidOperationException($"Cannot open Event '{name}' after retries.");
        }
    }
}
