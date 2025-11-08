using Faster.Transport.Primitives;
using System.Runtime.CompilerServices;

public sealed class InprocLink
{
    public MpscQueue<ArraySegment<byte>> ToServer { get; }
    public MpscQueue<ArraySegment<byte>> ToClient { get; }

    // Separate signals so client and server don't overwrite each other
    public Action? OnServerDataAvailable { get; set; } // wakes server-side particle
    public Action? OnClientDataAvailable { get; set; } // wakes client-side particle

    public InprocLink(int capacity)
    {
        ToServer = new MpscQueue<ArraySegment<byte>>(capacity);
        ToClient = new MpscQueue<ArraySegment<byte>>(capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SignalServerPeer() => OnServerDataAvailable?.Invoke();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SignalClientPeer() => OnClientDataAvailable?.Invoke();
}
