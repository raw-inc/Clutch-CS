using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.Testing.Performance;

/// <summary>試験が所有するプロセスの累積CPU・I/Oと、その時点の使用量です。I/Oは物理ディスク書込み量とは区別します。</summary>
internal sealed record ProcessResourceSample(int ProcessId, DateTimeOffset At, double CpuMilliseconds, long PrivateBytes, long WorkingSetBytes,
    long? ReadBytes, long? WriteBytes, long? ReadOperations, long? WriteOperations, long? OtherOperations)
{
    /// <summary>強制GCやサービス設定変更をせず、指定した実プロセスのカウンターを取得します。</summary>
    /// <returns>WindowsではプロセスI/O、Linuxではrchar/wchar、他OSではI/O未計測の値です。</returns>
    internal static ProcessResourceSample Capture(int id)
    {
        using Process process = Process.GetProcessById(id);
        long? read = null, write = null;
        long? reads = null, writes = null, other = null;
        if (OperatingSystem.IsWindows())
        {
            (read, write, reads, writes, other) = ProcessIoNative.Capture(process.Handle);
        }
        if (OperatingSystem.IsLinux())
        {
            string readKey = "rchar", writeKey = "wchar", readsKey = "syscr", writesKey = "syscw";
            foreach (string line in File.ReadLines("/proc/" + id.ToString(CultureInfo.InvariantCulture) + "/io"))
            {
                string[] pair = line.Split(':', 2);
                if (string.Equals(pair[0], readKey, StringComparison.Ordinal))
                {
                    read = long.Parse(pair[1], CultureInfo.InvariantCulture);
                }
                if (string.Equals(pair[0], writeKey, StringComparison.Ordinal))
                {
                    write = long.Parse(pair[1], CultureInfo.InvariantCulture);
                }
                if (string.Equals(pair[0], readsKey, StringComparison.Ordinal))
                {
                    reads = long.Parse(pair[1], CultureInfo.InvariantCulture);
                }
                if (string.Equals(pair[0], writesKey, StringComparison.Ordinal))
                {
                    writes = long.Parse(pair[1], CultureInfo.InvariantCulture);
                }
            }
        }
        return new(id, DateTimeOffset.UtcNow, process.TotalProcessorTime.TotalMilliseconds, process.PrivateMemorySize64, process.WorkingSet64, read, write, reads, writes, other);
    }
}

/// <summary>試験対象のWindowsプロセスからI/O累積値だけを取得します。</summary>
internal static partial class ProcessIoNative
{
    /// <summary>既に所有を確認したプロセスハンドルを同期的に参照します。</summary>
    /// <returns>累積転送バイト数とI/O操作回数です。</returns>
    internal static (long Read, long Write, long ReadOperations, long WriteOperations, long OtherOperations) Capture(nint process)
    {
        if (!GetProcessIoCounters(process, out Counters counters))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        return (checked((long)counters.ReadBytes), checked((long)counters.WriteBytes), checked((long)counters.ReadOperations),
            checked((long)counters.WriteOperations), checked((long)counters.OtherOperations));
    }

    /// <summary>Windowsが提供するプロセスI/Oの累積値を読みます。</summary>
    /// <returns>カウンターを取得できたかです。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(nint process, out Counters counters);

    /// <summary>IO_COUNTERSの固定幅・順序に一致する値です。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Counters(ulong ReadOperations, ulong WriteOperations, ulong OtherOperations, ulong ReadBytes, ulong WriteBytes, ulong OtherBytes);
}
