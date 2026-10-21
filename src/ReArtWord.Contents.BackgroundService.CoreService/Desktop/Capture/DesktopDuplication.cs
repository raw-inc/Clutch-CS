using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.CoreService.Windows;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Capture;

/// <summary>FFmpegの既定DXGI adapter上のoutputと物理画面名を照合します。</summary>
[SupportedOSPlatform("windows")]
internal static partial class DesktopDuplication
{
    /// <summary>列挙順をGDIの画面順と混同せず、対応するoutputだけを使用します。</summary>
    /// <returns>回転していない既定adapterのoutput番号、またはGDIが必要なnullです。</returns>
    internal static int? FindOutput(string name)
    {
        Guid factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
        int result = CreateDXGIFactory1(ref factoryId, out IntPtr factory);
        if (result < 0)
        {
            return null;
        }
        try
        {
            EnumItem adapters = WindowsCom.Method<EnumItem>(factory, 12);
            if (adapters(factory, 0, out IntPtr adapter) < 0)
            {
                return null;
            }
            try
            {
                return FindOutput(adapter, name);
            }
            finally
            {
                Marshal.Release(adapter);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    /// <summary>DeviceNameとrotationが一致したoutputの番号を返します。</summary>
    /// <returns>FFmpeg ddagrabで指定できる番号です。</returns>
    private static int? FindOutput(IntPtr adapter, string name)
    {
        EnumItem outputs = WindowsCom.Method<EnumItem>(adapter, 7);
        IntPtr description = Marshal.AllocHGlobal(96);
        try
        {
            for (uint index = 0; index < 32; index++)
            {
                if (outputs(adapter, index, out IntPtr output) < 0)
                {
                    break;
                }
                try
                {
                    GetDescription describe = WindowsCom.Method<GetDescription>(output, 7);
                    if (0 <= describe(output, description) && Marshal.ReadInt32(description, 80) != 0
                        && Marshal.ReadInt32(description, 84) == 1 && string.Equals(Marshal.PtrToStringUni(description), name, StringComparison.Ordinal))
                    {
                        return checked((int)index);
                    }
                }
                finally
                {
                    Marshal.Release(output);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(description);
        }
        return null;
    }

    /// <summary>既知interfaceのadapterまたはoutputを列挙します。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumItem(IntPtr instance, uint index, out IntPtr item);

    /// <summary>DXGI_OUTPUT_DESCの固定構造を取得します。</summary>
    /// <returns>HRESULTです。</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription(IntPtr instance, IntPtr description);

    /// <summary>IDXGIFactory1を生成します。</summary>
    /// <returns>HRESULTです。</returns>
    [LibraryImport("dxgi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateDXGIFactory1(ref Guid interfaceId, out IntPtr factory);
}
