using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture.PacketMonitor;

/// <summary>公開 PktMon ABI のソース生成相互運用です。System32 の DLL のみを使用します。</summary>
internal static partial class PktMonNativeDangerous
{
    /// <summary>HRESULT を利用者向けエラーへ変換します。</summary>
    internal static void Check(int result)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"Packet Monitor: HRESULT 0x{result:X8}. 0x80070005 requires administrator privileges. Select Npcap if this Windows installation does not support the API.");
        }
    }

    /// <summary>初期化します。</summary>
    /// <returns>HRESULT。</returns>
    [LibraryImport("pktmonapi.dll", EntryPoint = "PacketMonitorInitialize")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int Initialize(uint version, nint reserved, out nint handle);

    /// <summary>初期化ハンドルを解放します。</summary>
    [LibraryImport("pktmonapi.dll", EntryPoint = "PacketMonitorUninitialize")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial void Uninitialize(nint handle);

    /// <summary>独立セッションを作成します。</summary>
    /// <returns>HRESULT。</returns>
    [LibraryImport("pktmonapi.dll", EntryPoint = "PacketMonitorCreateLiveSession", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int CreateSession(nint handle, string name, out nint session);

    /// <summary>対象を列挙します。BOOLEAN は 1 byte です。</summary>
    /// <returns>HRESULT。</returns>
    [LibraryImport("pktmonapi.dll", EntryPoint = "PacketMonitorEnumDataSources")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int Enumerate(nint handle, int kind, byte hidden, nuint capacity, out nuint needed, nint buffer);

    /// <summary>キャプチャ対象を指定します。</summary>
    /// <returns>HRESULT。</returns>
    [LibraryImport("pktmonapi.dll", EntryPoint = "PacketMonitorAddSingleDataSourceToSession")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int AddSource(nint session, nint value);

    /// <summary>出力ストリームを指定します。</summary>
    /// <returns>HRESULT。</returns>
    [LibraryImport("pktmonapi.dll", EntryPoint = "PacketMonitorAttachOutputToSession")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int Attach(nint session, nint value);

    /// <summary>セッションを開始・停止します。</summary>
    /// <returns>HRESULT。</returns>
    [LibraryImport("pktmonapi.dll", EntryPoint = "PacketMonitorSetSessionActive")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int SetActive(nint session, byte active);

    /// <summary>リアルタイムストリームを作成します。</summary>
    /// <returns>HRESULT。</returns>
    [LibraryImport("pktmonapi.dll", EntryPoint = "PacketMonitorCreateRealtimeStream")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int CreateStream(nint handle, ref StreamConfigurationDangerous configuration, out nint stream);

    /// <summary>ストリームと進行中のコールバックを終了します。</summary>
    [LibraryImport("pktmonapi.dll", EntryPoint = "PacketMonitorCloseRealtimeStream")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial void CloseStream(nint stream);

    /// <summary>自身のセッションのみを解放します。</summary>
    [LibraryImport("pktmonapi.dll", EntryPoint = "PacketMonitorCloseSessionHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial void CloseSession(nint session);

    /// <summary>受信 ABI。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void DataCallback(nint context, nint descriptor);

    /// <summary>状態通知 ABI。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void EventCallback(nint context, nint info, int kind);
}
