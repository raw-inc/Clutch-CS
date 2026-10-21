using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Audio;

/// <summary>WASAPI固定interfaceの同期呼出し署名です。</summary>
internal static class WindowsAudioNative
{
    /// <summary>既定の再生deviceを取得します。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int DefaultEndpoint(IntPtr enumerator, int flow, int role, out IntPtr device);

    /// <summary>再生deviceのIAudioClientを開きます。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int Activate(IntPtr device, ref Guid interfaceId, uint context, IntPtr parameters, out IntPtr client);

    /// <summary>共有loopbackと既定format変換を設定します。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int Initialize(IntPtr client, int mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);

    /// <summary>buffer到着eventを設定します。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetEvent(IntPtr client, IntPtr handle);

    /// <summary>capture interfaceを借用します。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetService(IntPtr client, ref Guid interfaceId, out IntPtr service);

    /// <summary>開始・停止を呼び出します。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int Control(IntPtr client);

    /// <summary>次のpacketのsample frame数を読みます。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetSize(IntPtr capture, out uint frames);

    /// <summary>packetと取得時のQPCを借用します。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetBuffer(IntPtr capture, out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong qpc);

    /// <summary>取得スレッドからpacketを返却します。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ReleaseBuffer(IntPtr capture, uint frames);
}
