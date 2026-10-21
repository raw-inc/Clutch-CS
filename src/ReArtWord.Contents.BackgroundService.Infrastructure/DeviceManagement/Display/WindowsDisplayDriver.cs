using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Display;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Display;

/// <summary>コンソールの表示デバイスとドライバーのDEVMODEを固定して扱います。</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDisplayDriver : IWindowsDisplayDriver
{
    private static int DeviceBytes => 840;
    private static int ModeBytes => 220;
    private static int ModeSizeOffset => 68;
    private static int ModeFieldsOffset => 72;
    private static int ModeColorOffset => 168;
    private static int ModeWidthOffset => 172;
    private static int ModeHeightOffset => 176;
    private static int ModeFlagsOffset => 180;
    private static int ModeFrequencyOffset => 184;

    /// <summary>サービスのSession 0や別のRDPセッションを物理表示として扱いません。</summary>
    /// <returns>ドライバーが現在のデスクトップへ接続している表示です。</returns>
    public IReadOnlyList<DeviceDisplayInfo> Read()
    {
        using Process process = Process.GetCurrentProcess();
        int session = process.SessionId;
        if (session == 0 || WTSGetActiveConsoleSessionId() != session)
        {
            throw new InvalidOperationException("Device.DisplaySessionUnavailable");
        }
        List<DeviceDisplayInfo> displays = [];
        for (int index = 0; index < 64; index++)
        {
            DisplayDevice? device = Device(null, index);
            if (device is null)
            {
                return displays.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            }
            if ((device.Flags & 1) == 0 || (device.Flags & 8) != 0)
            {
                continue;
            }
            List<DisplayDevice> monitors = [];
            for (int monitor = 0; monitor < 64; monitor++)
            {
                DisplayDevice? attached = Device(device.Name, monitor);
                if (attached is null)
                {
                    break;
                }
                if ((attached.Flags & 1) != 0)
                {
                    monitors.Add(attached);
                }
            }
            if (monitors.Count == 0 || monitors.Any(item => string.IsNullOrWhiteSpace(item.Id)))
            {
                throw new InvalidOperationException("Device.DisplayIdentityUnavailable");
            }
            DeviceDisplayMode current = Mode(device.Name, -1) ?? throw new InvalidOperationException("Device.DisplayReadFailed");
            string id = ContractJson.Hash(new { device.Id, Monitors = monitors.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray() });
            displays.Add(new(id, device.Name, string.Join(" / ", monitors.Select(item => item.Description)), (device.Flags & 4) != 0, session, current, Modes(device.Name)));
        }
        throw new InvalidOperationException("Device.DisplayEnumerationLimit");
    }

    /// <summary>表示ドライバーへ副作用なしの検証を要求します。</summary>
    /// <returns>DISP_CHANGE結果コードです。</returns>
    public int Test(string device, DeviceDisplayMode mode) => Change(device, mode, 2);

    /// <summary>位置・回転・主画面指定を変更せず、モードを保存します。</summary>
    /// <returns>DISP_CHANGE結果コードです。</returns>
    public int Apply(string device, DeviceDisplayMode mode) => Change(device, mode, 1);

    /// <summary>表示アダプターまたはその接続モニターを一件取得します。</summary>
    /// <returns>一覧の終端はnullです。</returns>
    private static DisplayDevice? Device(string? parent, int index)
    {
        IntPtr buffer = Marshal.AllocHGlobal(DeviceBytes);
        try
        {
            Marshal.Copy(new byte[DeviceBytes], 0, buffer, DeviceBytes);
            Marshal.WriteInt32(buffer, DeviceBytes);
            if (!EnumDisplayDevicesW(parent, index, buffer, 1))
            {
                return null;
            }
            return new(Marshal.PtrToStringUni(buffer + 4)!, Marshal.PtrToStringUni(buffer + 68)!, Marshal.PtrToStringUni(buffer + 328)!, Marshal.ReadInt32(buffer, 324));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>DEVMODEWの固定部分から、公開する表示モードを読みます。</summary>
    /// <returns>現在値または候補。列挙終端はnullです。</returns>
    private static DeviceDisplayMode? Mode(string device, int index)
    {
        IntPtr buffer = AllocateMode();
        try
        {
            if (!EnumDisplaySettingsExW(device, index, buffer, 0))
            {
                return null;
            }
            return new(Marshal.ReadInt32(buffer, ModeWidthOffset), Marshal.ReadInt32(buffer, ModeHeightOffset), Marshal.ReadInt32(buffer, ModeFrequencyOffset),
                Marshal.ReadInt32(buffer, ModeColorOffset), unchecked((uint)Marshal.ReadInt32(buffer, ModeFlagsOffset)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>ドライバーが返した有限の候補だけを重複なしで公開します。</summary>
    /// <returns>寸法・Hz・色深度・フラグ順のモード一覧です。</returns>
    private static DeviceDisplayMode[] Modes(string device)
    {
        HashSet<DeviceDisplayMode> modes = [];
        for (int index = 0; index < 4096; index++)
        {
            DeviceDisplayMode? mode = Mode(device, index);
            if (mode is null)
            {
                return modes.OrderBy(item => item.Width).ThenBy(item => item.Height).ThenBy(item => item.RefreshRate).ThenBy(item => item.BitsPerPixel).ThenBy(item => item.Flags).ToArray();
            }
            if (2 <= mode.RefreshRate && 8 <= mode.BitsPerPixel && mode.Flags <= 3)
            {
                modes.Add(mode);
            }
        }
        throw new InvalidOperationException("Device.DisplayEnumerationLimit");
    }

    /// <summary>要求された公開モードに対応するドライバー構造体を使います。</summary>
    /// <returns>対象以外の項目を変更しないOSの結果です。</returns>
    private static int Change(string device, DeviceDisplayMode mode, uint flags)
    {
        IntPtr buffer = AllocateMode();
        try
        {
            for (int index = 0; index < 4096; index++)
            {
                if (!EnumDisplaySettingsExW(device, index, buffer, 0))
                {
                    break;
                }
                if (Marshal.ReadInt32(buffer, ModeWidthOffset) != mode.Width || Marshal.ReadInt32(buffer, ModeHeightOffset) != mode.Height
                    || Marshal.ReadInt32(buffer, ModeFrequencyOffset) != mode.RefreshRate || Marshal.ReadInt32(buffer, ModeColorOffset) != mode.BitsPerPixel
                    || unchecked((uint)Marshal.ReadInt32(buffer, ModeFlagsOffset)) != mode.Flags)
                {
                    continue;
                }
                Marshal.WriteInt32(buffer, ModeFieldsOffset, 0x7C0000);
                return ChangeDisplaySettingsExW(device, buffer, IntPtr.Zero, flags, IntPtr.Zero);
            }
            return -2;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Windowsが要求するサイズを設定したDEVMODEWを確保します。</summary>
    /// <returns>呼出元が必ず解放するネイティブ領域です。</returns>
    private static IntPtr AllocateMode()
    {
        IntPtr buffer = Marshal.AllocHGlobal(ModeBytes);
        Marshal.Copy(new byte[ModeBytes], 0, buffer, ModeBytes);
        Marshal.WriteInt16(buffer, ModeSizeOffset, checked((short)ModeBytes));
        return buffer;
    }

    /// <summary>Windowsの表示接続識別子です。</summary>
    private sealed record DisplayDevice(string Name, string Description, string Id, int Flags);

    /// <summary>現在の対話コンソールのセッションを取得します。</summary>
    /// <returns>セッション識別子、未取得時はUInt32.MaxValueです。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WTSGetActiveConsoleSessionId();

    /// <summary>接続された表示デバイスを列挙します。</summary>
    /// <returns>指定番号にデバイスが存在するかです。</returns>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayDevicesW(string? device, int index, IntPtr information, uint flags);

    /// <summary>表示ドライバーが公開したモードを取得します。</summary>
    /// <returns>指定モードが存在するかです。</returns>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettingsExW(string device, int mode, IntPtr settings, uint flags);

    /// <summary>ドライバー検証または指定モードの適用を行います。</summary>
    /// <returns>DISP_CHANGE結果コードです。</returns>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ChangeDisplaySettingsExW(string device, IntPtr mode, IntPtr window, uint flags, IntPtr parameter);
}
