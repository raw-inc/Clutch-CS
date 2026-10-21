using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>物理NICの64bit転送カウンターを、外部プロセスを起動せず取得します。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsNetworkThroughput
{
    /// <summary>仮想NIC・VPN・フィルターを合算対象から除きます。</summary>
    /// <returns>物理NICごとの累積値、照会失敗ならnullです。</returns>
    internal static IReadOnlyList<TransferCounter>? Read()
    {
        IntPtr table = IntPtr.Zero;
        try
        {
            if (GetIfTable2(out table) != 0 || table == IntPtr.Zero)
            {
                return null;
            }
            int count = Marshal.ReadInt32(table);
            if (count < 0 || 65536 < count)
            {
                return null;
            }
            List<TransferCounter> values = [];
            int size = Marshal.SizeOf<WindowsInterfaceRow>();
            for (int index = 0; index < count; index++)
            {
                // MIB_IF_TABLE2は4byte件数の後、8byte境界で最初の行が始まります。
                WindowsInterfaceRow row = Marshal.PtrToStructure<WindowsInterfaceRow>(IntPtr.Add(table, 8 + index * size));
                if (row.Counter() is { } counter)
                {
                    values.Add(counter);
                }
            }
            return values;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                FreeMibTable(table);
            }
        }
    }

    /// <summary>OSが管理する全インターフェースの一覧を読み取ります。</summary>
    /// <returns>Win32状態コードです。</returns>
    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetIfTable2(out IntPtr table);

    /// <summary>OSが確保した一覧を必ず解放します。</summary>
    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void FreeMibTable(IntPtr table);
}
