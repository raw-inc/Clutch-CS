using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.EnvironmentVariables;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.EnvironmentVariables;

/// <summary>サービスのHKCUを使わず、Machineまたは明示した読込み済みSIDを編集します。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsEnvironmentVariables
{
    private static string UserScope => "User";
    private static string MachineScope => "Machine";
    private static string EnvironmentKey => "Environment";
    private static string MachineKey => @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>一括画面でも暗黙のユーザー切替を行わないための選択肢です。</summary>
    /// <returns>環境設定先を特定できる読込み済み利用者です。</returns>
    internal static IReadOnlyList<DeviceUserProfile> Users()
    {
        using RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        List<DeviceUserProfile> result = [];
        string[] prefixes = ["S-1-5-21-", "S-1-12-1-"];
        foreach (string sid in users.GetSubKeyNames().Where(name => prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))
            && name.All(character => char.IsAsciiDigit(character) || character is 'S' or '-')).Order(StringComparer.Ordinal))
        {
            string name = sid;
            try
            {
                name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            }
            catch (IdentityNotMappedException)
            {
                // アカウント名が引けなくても、明示SIDの選択は保持します。
            }
            result.Add(new(sid, name));
        }
        return result;
    }

    /// <summary>値を展開せず、指定スコープだけの内容版を作ります。</summary>
    /// <returns>一覧または一つの値の取得結果です。</returns>
    internal static DeviceSnapshot Read(DeviceReadRequest request)
    {
        using RegistryKey? key = Open(request, false);
        List<DeviceEnvironmentVariable> variables = [];
        foreach (string name in (key?.GetValueNames() ?? []).Order(StringComparer.OrdinalIgnoreCase))
        {
            RegistryValueKind kind = key!.GetValueKind(name);
            if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            {
                throw new InvalidDataException("Device.UnsupportedEnvironmentValue");
            }
            string value = (string?)key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? string.Empty;
            DeviceEnvironmentVariable item = new(name, value, kind == RegistryValueKind.ExpandString);
            variables.Add(item with { Revision = ContractJson.Hash(item) });
        }
        if (request.ResourceId is not null)
        {
            DeviceEnvironmentVariable? item = variables.SingleOrDefault(variable => string.Equals(variable.Name, request.ResourceId, StringComparison.OrdinalIgnoreCase));
            return DeviceSnapshots.Create(item is null ? null
                                                       : item with { Revision = null });
        }
        return DeviceSnapshots.Create(new DeviceEnvironmentState(request.Scope, request.UserSid, variables));
    }

    /// <summary>取得済み版を再確認してから一つの値だけを書き換えます。</summary>
    /// <returns>永続化後のセッション通知をWindowsが確認したかです。</returns>
    internal static bool Apply(DeviceReadRequest request, DeviceEnvironmentChange change, string expectedRevision)
    {
        if (!string.Equals(Read(request).Revision, expectedRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Device.SettingsChanged");
        }
        using RegistryKey key = Open(request, true) ?? throw new UnauthorizedAccessException("Device.PermissionDenied");
        if (change.Delete)
        {
            key.DeleteValue(request.ResourceId!, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(request.ResourceId!, change.Value!, change.Expandable ? RegistryValueKind.ExpandString
                                                                               : RegistryValueKind.String);
        }
        key.Flush();
        return SendMessageTimeout(new IntPtr(0xffff), 0x001a, UIntPtr.Zero, EnvironmentKey, 2, 1000, out _) != IntPtr.Zero;
    }

    /// <summary>利用者が確認したSIDをHKEY_USERSへ限定します。</summary>
    /// <returns>指定範囲のキーです。存在しないUser環境一覧はnullです。</returns>
    private static RegistryKey? Open(DeviceReadRequest request, bool write)
    {
        if (string.Equals(request.Scope, MachineScope, StringComparison.Ordinal))
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return machine.OpenSubKey(MachineKey, write);
        }
        if (!string.Equals(request.Scope, UserScope, StringComparison.Ordinal) || !Users().Any(user => string.Equals(user.Sid, request.UserSid, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Device.UserUnavailable");
        }
        using RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using RegistryKey profile = users.OpenSubKey(request.UserSid!, write) ?? throw new InvalidOperationException("Device.UserUnavailable");
        return write ? profile.CreateSubKey(EnvironmentKey, true)
                     : profile.OpenSubKey(EnvironmentKey, false);
    }

    /// <summary>現在のセッションへ設定変更を通知します。別セッションの既存環境は置換しません。</summary>
    /// <returns>Windowsが返した通知結果です。</returns>
    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr parameter, string value, uint flags, uint timeout, out UIntPtr result);
}
