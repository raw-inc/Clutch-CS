using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Capture;

/// <summary>対話ユーザー内で物理画面を列挙します。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsDesktop
{
    /// <summary>DPI仮想化を避け、入力と映像で同じ物理座標を使います。</summary>
    /// <returns>接続時の画面構成です。</returns>
    internal static DesktopMonitor[] Monitors()
    {
        using DesktopDpiScope dpi = new();
        List<DesktopMonitor> monitors = [];
        MonitorCallback callback = (handle, _, _, _) =>
        {
            IntPtr info = Marshal.AllocHGlobal(104);
            try
            {
                Marshal.WriteInt32(info, 104);
                if (!GetMonitorInfoW(handle, info))
                {
                    return false;
                }
                string name = Marshal.PtrToStringUni(info + 40) ?? string.Empty;
                monitors.Add(ReadPhysicalMode(monitors.Count, name, (Marshal.ReadInt32(info, 36) & 1) != 0));
                return monitors.Count <= 32;
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        };
        bool enumerated = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        if (32 < monitors.Count)
        {
            throw new InvalidOperationException("At most 32 desktop monitors are supported.");
        }
        if (!enumerated || monitors.Count == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return monitors.ToArray();
    }

    /// <summary>DPIで仮想化されない表示モードから映像と入力の物理原点・寸法を固定します。</summary>
    /// <returns>現在の物理解像度と仮想desktop内の原点です。</returns>
    private static DesktopMonitor ReadPhysicalMode(int id, string name, bool primary)
    {
        IntPtr mode = Marshal.AllocHGlobal(220);
        try
        {
            Marshal.Copy(new byte[220], 0, mode, 220);
            Marshal.WriteInt16(mode, 68, 220);
            if (!EnumDisplaySettingsExW(name, -1, mode, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            int width = Marshal.ReadInt32(mode, 172), height = Marshal.ReadInt32(mode, 176);
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("A physical desktop display mode is unavailable.");
            }
            return new(id, name, Marshal.ReadInt32(mode, 76), Marshal.ReadInt32(mode, 80), width, height, primary);
        }
        finally
        {
            Marshal.FreeHGlobal(mode);
        }
    }

    /// <summary>DEVMODEWの現在の物理画面設定を取得します。</summary>
    /// <returns>取得成功です。</returns>
    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettingsExW(string device, int mode, IntPtr settings, int flags);

    /// <summary>表示デバイスごとの同期callbackです。</summary>
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr device, IntPtr rectangle, IntPtr data);

    /// <summary>物理モニターを列挙します。</summary>
    /// <returns>Win32 APIの実行結果です。</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(IntPtr device, IntPtr clip, MonitorCallback callback, IntPtr data);

    /// <summary>MONITORINFOEXを取得します。</summary>
    /// <returns>Win32 APIの実行結果です。</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr monitor, IntPtr info);

}
