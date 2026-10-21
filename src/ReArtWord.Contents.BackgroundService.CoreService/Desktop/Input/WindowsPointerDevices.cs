using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Input;

/// <summary>対話セッションのRaw Input一覧から、ホストに列挙されたマウスの有無を確認します。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsPointerDevices
{
    /// <summary>RDP転送入力を含まない一覧を読み、取得失敗をマウス未接続と混同しません。</summary>
    /// <returns>マウスあり、なし、確認不能の三状態です。</returns>
    internal static bool? Read()
    {
        // RAWINPUTDEVICELISTはHANDLE、DWORD、ポインター幅への末尾パディングです。
        uint size = (uint)(IntPtr.Size * 2);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint count = 0;
            if (GetRawInputDeviceList(IntPtr.Zero, ref count, size) == uint.MaxValue || 4096 < count)
            {
                return null;
            }
            if (count == 0)
            {
                return false;
            }
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)(count * size)));
            try
            {
                uint available = count;
                uint received = GetRawInputDeviceList(buffer, ref available, size);
                if (received == uint.MaxValue && Marshal.GetLastPInvokeError() == 122)
                {
                    // 列挙中のホットプラグでは確保済みの領域を再利用せず、有限回だけ読み直します。
                    continue;
                }
                if (received == uint.MaxValue || count < received)
                {
                    return null;
                }
                for (uint index = 0; index < received; index++)
                {
                    if (Marshal.ReadInt32(buffer, checked((int)(index * size) + IntPtr.Size)) == 0)
                    {
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                // 各デバイスのHANDLEは借用であり、一覧用に確保したバッファだけを解放します。
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    /// <summary>WindowsのRaw Inputデバイス一覧を読み取ります。</summary>
    /// <returns>読み取った件数、またはUINT_MAXです。</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetRawInputDeviceList(IntPtr devices, ref uint count, uint size);
}
