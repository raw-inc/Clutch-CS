using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>全processor groupの物理コア数・論理CPU数とOS登録名を読み取ります。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsCpuInformation
{
    /// <summary>プロセスのaffinityやquotaをCPU本体の仕様に読み替えません。</summary>
    /// <returns>取得できたCPU情報です。</returns>
    internal static (double? Cores, double? Threads, string? Name) Read()
    {
        uint threads = GetActiveProcessorCount(ushort.MaxValue);
        using RegistryKey? processors = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor");
        List<string> names = [];
        string nameKey = "ProcessorNameString";
        foreach (string key in processors?.GetSubKeyNames() ?? [])
        {
            using RegistryKey? processor = processors!.OpenSubKey(key);
            if (processor?.GetValue(nameKey) is string name && !string.IsNullOrWhiteSpace(name))
            {
                names.Add(name.Trim());
            }
        }
        string? label = null;
        if (names.Count != 0)
        {
            label = string.Join(" / ", names.Distinct(StringComparer.Ordinal));
        }
        double? logical = null;
        if (threads != 0)
        {
            logical = threads;
        }
        return (ReadCores(), logical, label);
    }

    /// <summary>可変長のRelationProcessorCoreレコードを一つずつ数えます。</summary>
    /// <returns>OSが返す有効コア数、取得不能ならnullです。</returns>
    private static double? ReadCores()
    {
        uint length = 0;
        _ = GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
        if (length == 0 || 16777216 < length)
        {
            return null;
        }
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref length))
            {
                return null;
            }
            int offset = 0;
            int count = 0;
            while (offset < length)
            {
                if (length - offset < 8)
                {
                    return null;
                }
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 8 || length - offset < size || Marshal.ReadInt32(buffer, offset) != 0)
                {
                    return null;
                }
                count++;
                offset += size;
            }
            return count;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Windowsの全groupを含む有効論理CPU数です。</summary>
    /// <returns>取得不能時は0です。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetActiveProcessorCount(ushort groupNumber);

    /// <summary>公開OS APIの可変長CPU topologyを取得します。</summary>
    /// <returns>バッファ取得が成功したかです。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}
