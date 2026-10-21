using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>Windowsの共通CPUと物理メモリを公開OS APIで観測します。</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsResourceProbe
{
    private ulong? previousTotal;
    private ulong previousIdle;

    /// <summary>CPU差分と物理メモリの現在値を取得します。</summary>
    /// <returns>初回差分待ちと取得不能を含む実測値です。</returns>
    internal IReadOnlyList<ResourceMetric> Capture()
    {
        List<ResourceMetric> result = [];
        WindowsMemoryStatus memory = new() { Length = checked((uint)Marshal.SizeOf<WindowsMemoryStatus>()) };
        if (GlobalMemoryStatusEx(ref memory))
        {
            result.Add(new ResourceMetric("memory.used", "machine", memory.TotalPhysical - memory.AvailablePhysical, "bytes"));
            result.Add(new ResourceMetric("memory.available", "machine", memory.AvailablePhysical, "bytes"));
            result.Add(new ResourceMetric("memory.total", "machine", memory.TotalPhysical, "bytes"));
            result.Add(new ResourceMetric("memory.usage", "machine", memory.MemoryLoad, "percent"));
        }
        else
        {
            result.Add(new ResourceMetric("memory.usage", "machine", null, "percent", "resource.memoryUnavailable"));
        }
        double? usage = null;
        string? reason = "resource.cpuInitialSample";
        if (GetActiveProcessorGroupCount() == 1 && GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            ulong total = kernel + user;
            if (previousTotal.HasValue && previousTotal.Value < total && previousIdle <= idle)
            {
                double elapsed = total - previousTotal.Value;
                usage = Math.Clamp((elapsed - (idle - previousIdle)) / elapsed * 100, 0, 100);
                reason = null;
            }
            previousTotal = total;
            previousIdle = idle;
        }
        else
        {
            reason = "resource.cpuUnavailableOrMultipleProcessorGroups";
        }
        result.Add(new ResourceMetric("cpu.usage", "machine", usage, "percent", reason));
        return result;
    }

    /// <summary>Win32が公開する物理メモリ状態を取得します。</summary>
    /// <returns>取得が成功したかです。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref WindowsMemoryStatus buffer);

    /// <summary>Win32 CPU累積時間を取得します。</summary>
    /// <returns>取得が成功したかです。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    /// <summary>CPU観測範囲を誤認しないようgroup数を確認します。</summary>
    /// <returns>有効なprocessor group数です。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial ushort GetActiveProcessorGroupCount();
}
