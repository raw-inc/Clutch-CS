using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture.PacketMonitor;

/// <summary>公開 ABI の固定レイアウトです。x64/arm64 とも末尾のパディングを含みます。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StreamConfigurationDangerous
{
    /// <summary>未使用のユーザーコンテキスト。</summary>
    internal nint Context;

    /// <summary>状態通知の関数ポインター。</summary>
    internal nint Event;

    /// <summary>受信通知の関数ポインター。</summary>
    internal nint Data;

    /// <summary>256 KiB のバッファの倍率。</summary>
    internal ushort BufferMultiplier;

    /// <summary>一フレームのキャプチャ上限。</summary>
    internal ushort SnapLength;

    /// <summary>ポインターを露出させず設定値だけを表示します。</summary>
    /// <returns>ストリームの容量設定。</returns>
    public override string ToString() => $"Buffer x{BufferMultiplier}, SnapLength {SnapLength}";
}
