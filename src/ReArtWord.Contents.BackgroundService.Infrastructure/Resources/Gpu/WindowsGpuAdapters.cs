using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Gpu;

/// <summary>DXGIの現在の接続実装をLUIDで識別し、software rendererをGPUへ数えません。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsGpuAdapters
{
    /// <summary>hotplugも古い生成処理一覧へ固定せず、現在のhardwareだけを取得します。</summary>
    /// <returns>OSで確認した名前とLUIDです。</returns>
    internal static IReadOnlyList<GpuAdapterIdentity> Read()
    {
        Guid factoryId = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
        IntPtr factory = IntPtr.Zero;
        try
        {
            if (CreateDXGIFactory1(ref factoryId, out factory) != 0)
            {
                return [];
            }

            // IDXGIFactory1::EnumAdapters1とIDXGIAdapter1::GetDesc1の公開COM ABI slotです。
            EnumAdapters enumerate = Marshal.GetDelegateForFunctionPointer<EnumAdapters>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size));
            List<GpuAdapterIdentity> result = [];
            for (uint index = 0; index < 32; index++)
            {
                if (enumerate(factory, index, out IntPtr adapter) != 0)
                {
                    break;
                }
                try
                {
                    GetDescription describe = Marshal.GetDelegateForFunctionPointer<GetDescription>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size));
                    if (describe(adapter, out WindowsGpuAdapterDescription description) == 0 && (description.Flags & 2) == 0)
                    {
                        string luid = string.Create(CultureInfo.InvariantCulture, $"luid_0x{description.LuidHigh:X8}_0x{description.LuidLow:X8}");
                        result.Add(new(luid, description.Name, description.VendorId, description.DedicatedVideoMemory.ToUInt64(), description.SharedSystemMemory.ToUInt64()));
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
            return result;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return [];
        }
        finally
        {
            if (factory != IntPtr.Zero)
            {
                Marshal.Release(factory);
            }
        }
    }

    /// <summary>生成処理の参照を取得します。</summary>
    /// <returns>HRESULTです。</returns>
    [LibraryImport("dxgi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateDXGIFactory1(ref Guid id, out IntPtr factory);

    /// <summary>生成処理が所有する接続実装を列挙します。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter);

    /// <summary>接続実装の名前・LUID・hardware flagを取得します。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription(IntPtr adapter, out WindowsGpuAdapterDescription description);
}
