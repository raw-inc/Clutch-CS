using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

/// <summary>導入済み libpcap だけを読み込む、ソース生成の相互運用です。</summary>
internal static partial class PcapNativeDangerous
{
    private static string libraryName => "signalscope_pcap";
    private static readonly string[] linuxLibraryNames = { "libpcap.so.1", "libpcap.so.0.8", "libpcap.so" };

    /// <summary>この worker アセンブリに限って pcap の配置を解決します。</summary>
    static PcapNativeDangerous()
    {
        NativeLibrary.SetDllImportResolver(typeof(PcapNativeDangerous).Assembly, Resolve);
    }

    /// <summary>実行ファイルの作業ディレクトリから DLL を探索しません。</summary>
    /// <returns>実行OSのライブラリハンドル。</returns>
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, libraryName, StringComparison.Ordinal))
        {
            return 0;
        }
        if (OperatingSystem.IsWindows())
        {
            return NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll"), assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        }

        if (OperatingSystem.IsMacOS())
        {
            return NativeLibrary.Load("/usr/lib/libpcap.A.dylib");
        }
        foreach (string libraryName in linuxLibraryNames)
        {
            if (NativeLibrary.TryLoad(libraryName, assembly, DllImportSearchPath.SafeDirectories, out nint library))
            {
                return library;
            }
        }
        throw new DllNotFoundException("libpcap is not installed on this host.");
    }

    /// <summary>取得元一覧を確保します。</summary>
    /// <returns>成功時0。</returns>
    [LibraryImport("signalscope_pcap", EntryPoint = "pcap_findalldevs")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int FindAll(out nint devices, nint error);

    /// <summary>一覧を解放します。</summary>
    [LibraryImport("signalscope_pcap", EntryPoint = "pcap_freealldevs")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial void FreeAll(nint devices);

    /// <summary>最大9000 bytesの原本を取得するハンドルを開きます。</summary>
    /// <returns>失敗時0。</returns>
    [LibraryImport("signalscope_pcap", EntryPoint = "pcap_open_live", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint Open(string name, int snapLength, int promiscuous, int timeoutMilliseconds, nint error);

    /// <summary>停止時にブロックしない受信へ変更します。</summary>
    /// <returns>成功時0。</returns>
    [LibraryImport("signalscope_pcap", EntryPoint = "pcap_setnonblock")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int SetNonBlock(nint handle, int enabled, nint error);

    /// <summary>次の原本への借用ポインターを取得します。</summary>
    /// <returns>1が原本、0が待ち、負数が終了・エラー。</returns>
    [LibraryImport("signalscope_pcap", EntryPoint = "pcap_next_ex")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int Next(nint handle, out nint header, out nint data);

    /// <summary>リンク種別を取得します。</summary>
    /// <returns>pcap のリンク種別。</returns>
    [LibraryImport("signalscope_pcap", EntryPoint = "pcap_datalink")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int LinkType(nint handle);

    /// <summary>キャプチャ統計を取得します。</summary>
    /// <returns>成功時0。</returns>
    [LibraryImport("signalscope_pcap", EntryPoint = "pcap_stats")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int Statistics(nint handle, nint statistics);

    /// <summary>このハンドルだけを解放します。</summary>
    [LibraryImport("signalscope_pcap", EntryPoint = "pcap_close")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial void Close(nint handle);
}
