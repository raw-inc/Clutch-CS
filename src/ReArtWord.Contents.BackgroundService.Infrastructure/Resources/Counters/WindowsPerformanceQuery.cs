using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

/// <summary>言語中立のwildcard PDH 照会を一つ保持し、差分をOSに計算させます。</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPerformanceQuery : SafeHandleZeroOrMinusOneIsInvalid, IPerformanceCounterQuery
{
    private readonly IntPtr counter;
    private bool collected;
    private static uint MoreData => 0x800007D2;

    /// <summary>照会は使い回し、毎回プロセスやカウンター登録を作りません。</summary>
    internal WindowsPerformanceQuery(string counterPath) : base(true)
    {
        try
        {
            if (PdhOpenQueryW(IntPtr.Zero, UIntPtr.Zero, out IntPtr query) == 0)
            {
                SetHandle(query);
                if (PdhAddEnglishCounterW(query, counterPath, UIntPtr.Zero, out counter) != 0)
                {
                    Dispose();
                }
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            Dispose();
        }
    }

    /// <summary>未加工の値を一巡更新し、実体ごとの有効性を保持します。</summary>
    /// <returns>取得不能はnull、計算不能の実体はValue=nullです。</returns>
    public IReadOnlyList<PerformanceCounterValue>? Collect()
    {
        if (IsInvalid || IsClosed)
        {
            return null;
        }
        bool held = false;
        try
        {
            DangerousAddRef(ref held);
            if (PdhCollectQueryData(handle) != 0)
            {
                collected = false;
                return null;
            }
            bool initial = !collected;
            collected = true;
            List<PerformanceCounterValue>? values = ReadArray();
            if (initial && values is null)
            {
                return [];
            }
            return values;
        }
        finally
        {
            if (held)
            {
                DangerousRelease();
            }
        }
    }

    /// <summary>カウンター実体の増減によるバッファー変化を有限回だけ読み直します。</summary>
    /// <returns>同じcollectに属する配列です。</returns>
    private List<PerformanceCounterValue>? ReadArray()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint bytes = 0;
            uint status = PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, out uint count, IntPtr.Zero);
            if (status != MoreData || bytes == 0)
            {
                if (status == 0)
                {
                    return [];
                }
                return null;
            }
            if (4194304 < bytes || 65536 < count)
            {
                return null;
            }
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
            try
            {
                status = PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, out count, buffer);
                if (status == MoreData)
                {
                    continue;
                }
                int size = Marshal.SizeOf<WindowsPdhItem>();
                if (status != 0 || 65536 < count || bytes < (ulong)count * (ulong)size)
                {
                    return null;
                }
                List<PerformanceCounterValue> entries = [];
                for (int index = 0; index < count; index++)
                {
                    WindowsPdhItem item = Marshal.PtrToStructure<WindowsPdhItem>(IntPtr.Add(buffer, index * size));
                    string? name = Marshal.PtrToStringUni(item.Name);
                    if (name is null)
                    {
                        continue;
                    }
                    double? usage = null;
                    if (item.Counter.Status is 0 or 1)
                    {
                        usage = item.Counter.Value;
                    }
                    entries.Add(new(name, usage));
                }
                return entries;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    /// <summary>照会の破棄で所属カウンターも解放します。</summary>
    /// <returns>Win32が解放を確認したかです。</returns>
    protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;

    /// <summary>リアルタイム照会を開きます。</summary>
    /// <returns>PDH状態コードです。</returns>
    [LibraryImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PdhOpenQueryW(IntPtr source, UIntPtr user, out IntPtr query);

    /// <summary>英語名のカウンターをOSの表示言語に依存せず登録します。</summary>
    /// <returns>PDH状態コードです。</returns>
    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr user, out IntPtr counter);

    /// <summary>前回と現在の未加工の値を保持します。</summary>
    /// <returns>PDH状態コードです。</returns>
    [LibraryImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PdhCollectQueryData(IntPtr query);

    /// <summary>wildcardの実体ごとにDOUBLEを計算します。</summary>
    /// <returns>PDH状態コードです。</returns>
    [LibraryImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);

    /// <summary>照会を閉じます。</summary>
    /// <returns>PDH状態コードです。</returns>
    [LibraryImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PdhCloseQuery(IntPtr query);
}
