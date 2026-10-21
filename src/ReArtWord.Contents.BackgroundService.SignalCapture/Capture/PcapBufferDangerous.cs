using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

/// <summary>pcap のエラー・統計領域を呼び出しの寿命だけ所有します。</summary>
internal sealed class PcapBufferDangerous : IDisposable
{
    /// <summary>256 bytes の所有領域。</summary>
    internal nint Pointer
    {
        get;
    } = Marshal.AllocHGlobal(256);

    /// <summary>この領域だけを解放します。</summary>
    public void Dispose() => Marshal.FreeHGlobal(Pointer);
}
