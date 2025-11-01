using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Faster.Transport.Primitives;

internal static class MemoryMarshalHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T GetArrayDataReference<T>(T[] array)
    {
        // Equivalent to MemoryMarshal.GetArrayDataReference on newer TFMs
        // Requires System.Memory package on .NET Framework
        return ref MemoryMarshal.GetReference(array.AsSpan());
    }
}
