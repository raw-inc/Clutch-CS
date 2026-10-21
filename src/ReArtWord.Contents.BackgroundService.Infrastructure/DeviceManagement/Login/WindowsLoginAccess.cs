using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Login;

/// <summary>Windowsの自動ログイン設定を、資格情報を公開せず管理します。</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsLoginAccess : IWindowsLoginAccess
{
    private static string WinlogonPath => @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private static string SystemPolicyPath => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private static string PlaintextName => "DefaultPassword";

    /// <summary>LSAの内容や旧方式の平文を画面・履歴へ返しません。</summary>
    /// <returns>現在の対象、秘密値の有無、変更できるユーザーです。</returns>
    public DeviceLoginState Read()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(WinlogonPath);
        bool? enabled = key?.GetValue("AutoAdminLogon") switch { null or "0" => false, "1" => true, _ => null };
        string name = key?.GetValue("DefaultUserName") as string ?? string.Empty;
        string domain = key?.GetValue("DefaultDomainName") as string ?? string.Empty;
        IReadOnlyList<DeviceLoginUser> users = WindowsLoginUsers.Read();
        string local = ".";
        bool localDomain = string.IsNullOrEmpty(domain) || string.Equals(domain, local, StringComparison.Ordinal)
            || string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        string? sid = null;
        if (localDomain)
        {
            sid = users.FirstOrDefault(user => string.Equals(user.Name, name, StringComparison.OrdinalIgnoreCase))?.Sid;
        }
        bool plaintext = key?.GetValueNames().Contains(PlaintextName, StringComparer.OrdinalIgnoreCase) ?? false;
        int? remaining = key?.GetValue("AutoLogonCount") as int?;
        return new(enabled, name, domain, sid, WindowsLoginSecret.Exists(), plaintext, remaining, users, Restrictions(key));
    }

    /// <summary>対象SIDの対話ログインだけを確認し、プロセスやデスクトップは起動しません。</summary>
    /// <returns>Windowsが資格情報と対象SIDを確認したかです。</returns>
    public bool Verify(DeviceLoginUser user, char[] password)
    {
        using LoginPasswordBuffer buffer = new(password);
        bool success = LogonUserW(user.Name, Environment.MachineName, buffer.Pointer, 2, 0, out SafeAccessTokenHandle token);
        using (token)
        {
            if (!success)
            {
                return false;
            }
            using WindowsIdentity identity = new(token.DangerousGetHandle());
            return string.Equals(identity.User?.Value, user.Sid, StringComparison.Ordinal);
        }
    }

    /// <summary>有効化を最後に実行できるよう、対象保存と分離します。</summary>
    public void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(WinlogonPath);
        string value = enabled ? "1"
                               : "0";
        key.SetValue("AutoAdminLogon", value, RegistryValueKind.String);
        key.Flush();
    }

    /// <summary>パスワードはLSAの保護された保存先だけへ渡します。</summary>
    public void SetPassword(char[]? password) => WindowsLoginSecret.Store(password);

    /// <summary>選択したローカルアカウントを固定し、回数による自動解除を除きます。</summary>
    public void SetUser(DeviceLoginUser user)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(WinlogonPath);
        key.SetValue("DefaultUserName", user.Name, RegistryValueKind.String);
        key.SetValue("DefaultDomainName", Environment.MachineName, RegistryValueKind.String);
        key.SetValue("AutoLogonSID", user.Sid, RegistryValueKind.String);
        key.DeleteValue("AutoLogonCount", false);
        key.Flush();
    }

    /// <summary>旧方式の値を読み出さず削除します。</summary>
    public void RemovePlaintextPassword()
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(WinlogonPath);
        key.DeleteValue(PlaintextName, false);
        key.Flush();
    }

    /// <summary>ポリシーと旧Winlogon設定のメッセージだけを明示要求に従って消去します。</summary>
    public void ClearLogonNotice()
    {
        using RegistryKey? system = Registry.LocalMachine.OpenSubKey(SystemPolicyPath, true);
        using RegistryKey? winlogon = Registry.LocalMachine.OpenSubKey(WinlogonPath, true);
        WindowsLoginNotice.Clear(system, winlogon);
    }

    /// <summary>自動ログインを止める既知のポリシーを無断で解除しません。</summary>
    /// <returns>有効化を妨げる条件です。</returns>
    private static List<string> Restrictions(RegistryKey? winlogon)
    {
        List<string> reasons = [];
        using RegistryKey? system = Registry.LocalMachine.OpenSubKey(SystemPolicyPath);
        if (WindowsLoginNotice.Exists(system, winlogon))
        {
            reasons.Add("Device.LoginNoticePolicy");
        }
        string smartCard = "scforceoption", passwordPolicy = "DevicePasswordEnabled";
        if (system?.GetValue(smartCard) is int force && force != 0)
        {
            reasons.Add("Device.LoginSmartCardPolicy");
        }
        using RegistryKey? eas = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\EAS\Policies");
        if (eas?.GetValue(passwordPolicy) is int required && required != 0)
        {
            reasons.Add("Device.LoginPasswordPolicy");
        }
        return reasons;
    }

    /// <summary>資格情報をコマンドラインへ渡さず、Windowsの対話認証を確認します。</summary>
    /// <returns>Windowsによる認証成否です。</returns>
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUserW(string user, string domain, IntPtr password, uint type, uint provider, out SafeAccessTokenHandle token);
}
