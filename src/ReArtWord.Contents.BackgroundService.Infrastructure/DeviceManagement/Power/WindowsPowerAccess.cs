using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

/// <summary>現在の電源プランを言語非依存のWindows APIで読み書きします。</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPowerAccess : IWindowsPowerAccess
{
    private static string StartupPath => @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    private static readonly Guid video = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid sleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid buttons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Dictionary<string, (Guid Group, Guid Setting)> settings = new()
    {
        [nameof(DevicePowerValues.DisplaySeconds)] = (video, new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e")),
        [nameof(DevicePowerValues.SleepSeconds)] = (sleep, new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da")),
        [nameof(DevicePowerValues.HibernateSeconds)] = (sleep, new("9d7815a6-7ee4-497e-8888-515a05f02364")),
        [nameof(DevicePowerValues.PowerButtonAction)] = (buttons, new("7648efa3-dd9c-4e3e-b566-50f929386280")),
        [nameof(DevicePowerValues.UnattendedSleepSeconds)] = (sleep, new("7bc4a2f9-d8fc-4469-b07b-33eb785aaca0")),
    };

    /// <summary>取得できない値を無効と読み替えず、そのまま未確認として返します。</summary>
    /// <returns>現在のプランとWindowsが報告した値です。</returns>
    public DevicePowerState Read()
    {
        Guid scheme = ActiveScheme();
        byte[] capabilities = new byte[76];
        if (!GetPwrCapabilities(capabilities))
        {
            throw new InvalidOperationException("Device.PowerReadFailed");
        }
        using RegistryKey? startup = Registry.LocalMachine.OpenSubKey(StartupPath);
        bool? fast = startup?.GetValue("HiberbootEnabled") switch { 0 => false, 1 => true, _ => null };
        bool canSleep = capabilities[3] != 0 || capabilities[4] != 0 || capabilities[5] != 0 || capabilities[20] != 0;
        bool canHibernate = capabilities[6] != 0 && capabilities[8] != 0;
        return new(scheme, FriendlyName(scheme), Values(scheme, true), Values(scheme, false), fast, canSleep, canHibernate,
            WindowsIdleShutdownStore.Read(), Restrictions());
    }

    /// <summary>読み取ったプランが現在も有効なときだけ、一つの項目を保存します。</summary>
    public void Write(Guid scheme, bool ac, string setting, uint value)
    {
        VerifyScheme(scheme);
        (Guid group, Guid id) = settings[setting];
        uint result = ac ? PowerWriteACValueIndex(IntPtr.Zero, in scheme, in group, in id, value)
                         : PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in group, in id, value);
        Check(result);
    }

    /// <summary>保存した電源プランをWindowsへ反映します。</summary>
    public void Activate(Guid scheme)
    {
        VerifyScheme(scheme);
        Check(PowerSetActiveScheme(IntPtr.Zero, in scheme));
    }

    /// <summary>休止状態そのものを無効化せず高速スタートアップを設定します。</summary>
    public void SetFastStartup(bool enabled)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(StartupPath);
        int value = enabled ? 1
                            : 0;
        key.SetValue("HiberbootEnabled", value, RegistryValueKind.DWord);
        key.Flush();
    }

    /// <summary>同一PCの全Coreで共有する設定を保存します。</summary>
    public void SetIdleShutdown(DeviceIdleShutdownPolicy policy) => WindowsIdleShutdownStore.Write(policy);

    /// <summary>電源プランが切り替わっていた場合に、別プランを編集しません。</summary>
    private static void VerifyScheme(Guid scheme)
    {
        if (ActiveScheme() != scheme)
        {
            throw new InvalidOperationException("Device.SettingsChanged");
        }
    }

    /// <summary>現在有効なプランを取得し、Windowsが確保したメモリを解放します。</summary>
    /// <returns>有効なプランのGUIDです。</returns>
    private static Guid ActiveScheme()
    {
        Check(PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer));
        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            _ = LocalFree(pointer);
        }
    }

    /// <summary>プラン名を有限バッファへ読みます。</summary>
    /// <returns>取得できなければ識別子です。</returns>
    private static string FriendlyName(Guid scheme)
    {
        byte[] buffer = new byte[4096];
        uint size = checked((uint)buffer.Length);
        if (PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
        {
            return scheme.ToString();
        }
        return Encoding.Unicode.GetString(buffer, 0, checked((int)size)).TrimEnd('\0');
    }

    /// <summary>AC・DCを混同せず、現在プランの設定を取得します。</summary>
    /// <returns>読取り可能な項目だけを含む値です。</returns>
    private static DevicePowerValues Values(Guid scheme, bool ac) => new(
        Value(scheme, ac, nameof(DevicePowerValues.DisplaySeconds)), Value(scheme, ac, nameof(DevicePowerValues.SleepSeconds)),
        Value(scheme, ac, nameof(DevicePowerValues.HibernateSeconds)), Value(scheme, ac, nameof(DevicePowerValues.PowerButtonAction)),
        Value(scheme, ac, nameof(DevicePowerValues.UnattendedSleepSeconds)));

    /// <summary>DWORDの設定値を取得します。</summary>
    /// <returns>Windowsが未対応・未確認とした場合はnullです。</returns>
    private static uint? Value(Guid scheme, bool ac, string setting)
    {
        (Guid group, Guid id) = settings[setting];
        uint value;
        uint result = ac ? PowerReadACValueIndex(IntPtr.Zero, in scheme, in group, in id, out value)
                         : PowerReadDCValueIndex(IntPtr.Zero, in scheme, in group, in id, out value);
        return result == 0 ? value
                           : null;
    }

    /// <summary>構成対象を上書きする管理ポリシーを検出します。</summary>
    /// <returns>管理されている設定の識別子です。</returns>
    private static List<string> Restrictions()
    {
        List<string> managed = [];
        string acKey = "ACSettingIndex", dcKey = "DCSettingIndex", startupKey = "HiberbootEnabled";
        foreach ((string name, (Guid _, Guid id)) in settings)
        {
            using RegistryKey? policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Power\PowerSettings\" + id);
            if (policy?.GetValue(acKey) is not null || policy?.GetValue(dcKey) is not null)
            {
                managed.Add(name);
            }
        }
        using RegistryKey? system = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System");
        if (system?.GetValue(startupKey) is not null)
        {
            managed.Add("FastStartup");
        }
        return managed;
    }

    /// <summary>Windowsの失敗コードを呼出元へ伝えます。</summary>
    private static void Check(uint code)
    {
        if (code != 0)
        {
            throw new Win32Exception(unchecked((int)code));
        }
    }

    /// <summary>有効なプランを取得します。</summary>
    /// <returns>Win32結果です。</returns>
    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    /// <summary>プランの名前を取得します。</summary>
    /// <returns>Win32結果です。</returns>
    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerReadFriendlyName(IntPtr root, in Guid scheme, IntPtr group, IntPtr setting, [Out] byte[] buffer, ref uint size);

    /// <summary>ACの設定を取得します。</summary>
    /// <returns>Win32結果です。</returns>
    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerReadACValueIndex(IntPtr root, in Guid scheme, in Guid group, in Guid setting, out uint value);

    /// <summary>DCの設定を取得します。</summary>
    /// <returns>Win32結果です。</returns>
    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerReadDCValueIndex(IntPtr root, in Guid scheme, in Guid group, in Guid setting, out uint value);

    /// <summary>ACの設定を保存します。</summary>
    /// <returns>Win32結果です。</returns>
    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerWriteACValueIndex(IntPtr root, in Guid scheme, in Guid group, in Guid setting, uint value);

    /// <summary>DCの設定を保存します。</summary>
    /// <returns>Win32結果です。</returns>
    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerWriteDCValueIndex(IntPtr root, in Guid scheme, in Guid group, in Guid setting, uint value);

    /// <summary>プランを有効化します。</summary>
    /// <returns>Win32結果です。</returns>
    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerSetActiveScheme(IntPtr root, in Guid scheme);

    /// <summary>SYSTEM_POWER_CAPABILITIESを取得します。</summary>
    /// <returns>取得成否です。</returns>
    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool GetPwrCapabilities([Out] byte[] capabilities);

    /// <summary>API所有の領域を解放します。</summary>
    /// <returns>成功時はnullです。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr LocalFree(IntPtr pointer);
}
