using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture.PacketMonitor;

/// <summary>PktMon が返す自己参照バッファを寿命内で検証・参照します。</summary>
internal sealed class PktMonSourcesDangerous : IDisposable
{
    private readonly nint buffer;
    private readonly int capacity;
    private readonly List<nint> pointers = new();
    private readonly List<CaptureAdapter> adapters = new();
    private readonly List<PktMonSourceIdentity> identities = new();
    private bool disposed;

    /// <summary>サイズ照会後に独自所有の領域へ列挙します。</summary>
    internal PktMonSourcesDangerous(nint handle, bool showHidden = false)
    {
        byte hidden = showHidden ? (byte)1
                                 : (byte)0;
        int result = PktMonNativeDangerous.Enumerate(handle, 1, hidden, 0, out nuint needed, 0);
        if (result < 0 && result != unchecked((int)0x8007007a))
        {
            PktMonNativeDangerous.Check(result);
        }

        if (needed < 8 || 16 * 1024 * 1024 < needed)
        {
            throw new InvalidOperationException("Invalid Packet Monitor data-source list size.");
        }

        capacity = checked((int)needed);
        buffer = Marshal.AllocHGlobal(capacity);
        bool complete = false;
        try
        {
            PktMonNativeDangerous.Check(PktMonNativeDangerous.Enumerate(handle, 1, hidden, needed, out nuint actual, buffer));
            if (actual < 8 || needed < actual)
            {
                throw new InvalidOperationException("Adapter configuration changed. Select an adapter again.");
            }

            foreach ((nint pointer, CaptureAdapter adapter, PktMonSourceIdentity identity)in PktMonSourceListDangerous.ReadDangerous(buffer, (int)actual))
            {
                pointers.Add(pointer);
                adapters.Add(adapter);
                identities.Add(identity);
            }

            complete = true;
        }
        finally
        {
            if (!complete)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>管理下で保持する対象一覧。</summary>
    internal IReadOnlyList<CaptureAdapter> Adapters => adapters;

    /// <summary>ネイティブ領域から独立した観測点の識別情報。</summary>
    internal IReadOnlyList<PktMonSourceIdentity> Identities => identities;

    /// <summary>識別名が一致する対象のポインターを返します。</summary>
    /// <returns>このオブジェクトの寿命内だけ有効なポインター。</returns>
    internal (nint Pointer, PktMonSourceIdentity Identity) Find(string id)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        for (int i = 0; i < adapters.Count; i++)
        {
            if (string.Equals(adapters[i].Id, id, StringComparison.Ordinal))
            {
                return (pointers[i], identities[i]);
            }
        }

        throw new InvalidOperationException("The selected adapter is no longer available. Select an adapter again.");
    }

    /// <summary>この列挙で所有する領域だけを解放します。</summary>
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            Marshal.FreeHGlobal(buffer);
        }
    }
}
