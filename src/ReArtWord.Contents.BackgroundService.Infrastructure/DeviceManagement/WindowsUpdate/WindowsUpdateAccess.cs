using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WindowsUpdate;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WindowsUpdate;

/// <summary>Windowsの更新設定、MDM・ドメイン管理、インストール状態を照会します。</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsUpdateAccess : IWindowsUpdateAccess
{
    private static readonly string[] editions = ["Professional", "Enterprise", "Education", "IoTEnterprise"];
    internal static string UpdatePath => @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    internal static string AutomaticPath => UpdatePath + @"\AU";
    internal static IReadOnlyList<string> DeadlineNames => ["SetComplianceDeadline", "SetComplianceDeadlineForQU", "SetComplianceDeadlineForFU", "SetAutoRestartDeadline"];

    /// <summary>未対応や組織管理を、手動更新の設定成功と取り違えません。</summary>
    /// <returns>ホストの更新管理状態です。</returns>
    public DeviceWindowsUpdateState Read()
    {
        using RegistryKey? version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string edition = version?.GetValue("EditionID") as string ?? string.Empty;
        bool supported = editions.Any(prefix => edition.StartsWith(prefix, StringComparison.Ordinal));
        using RegistryKey? update = Registry.LocalMachine.OpenSubKey(UpdatePath);
        using RegistryKey? automatic = Registry.LocalMachine.OpenSubKey(AutomaticPath);
        int? disabled = Number(automatic, "NoAutoUpdate");
        Dictionary<string, int?> deadlines = DeadlineNames.ToDictionary(name => name, name => Number(update, name), StringComparer.Ordinal);
        List<string> restrictions = ManagementRestrictions();
        if (disabled is not (null or 0 or 1) || deadlines.Values.Any(value => value is not (null or 0 or 1)))
        {
            restrictions.Add("Device.UpdatePolicyUnconfirmed");
        }
        using RegistryKey? reboot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        using RegistryKey? servicing = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        return new(edition, supported, disabled, deadlines, restrictions, reboot is not null || servicing is not null, Installing());
    }

    /// <summary>COMのローカルポリシーを一つのSTAで操作します。</summary>
    /// <returns>ポリシー保存と有効値への反映の完了です。</returns>
    public Task WriteAsync(DeviceWindowsUpdateChange change, Action<string> completed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                DeviceWindowsUpdateChange.Validate(Read());
                LocalUpdatePolicy.Write(change.ManualOnly, completed);
                completion.SetResult();
            }
            catch (Exception exception) when (exception is COMException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                completion.SetException(exception);
            }
        }) { IsBackground = true, Name = "Contents.WindowsUpdatePolicy" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>DWORD以外の設定を未設定に読み替えません。</summary>
    /// <returns>未設定ならnull、設定済みなら数値です。</returns>
    private static int? Number(RegistryKey? key, string name) => key?.GetValue(name) switch
    {
        null => null,
        int value => value,
        _ => throw new InvalidOperationException("Device.UpdatePolicyUnconfirmed"),
    };

    /// <summary>外部管理元との優先順位をローカル設定で上書きしません。</summary>
    /// <returns>管理元、または管理確認不能の理由です。</returns>
    private static List<string> ManagementRestrictions()
    {
        List<string> reasons = [];
        uint result = NetGetJoinInformation(null, out IntPtr name, out int status);
        if (name != IntPtr.Zero)
        {
            _ = NetApiBufferFree(name);
        }
        if (result != 0)
        {
            reasons.Add("Device.UpdateManagementUnconfirmed");
        }
        if (result == 0 && status == 3)
        {
            reasons.Add("Device.UpdateDomainManaged");
        }
        try
        {
            int mdm = IsDeviceRegisteredWithManagement(out bool registered, 0, IntPtr.Zero);
            if (mdm != 0)
            {
                reasons.Add("Device.UpdateManagementUnconfirmed");
            }
            if (mdm == 0 && registered)
            {
                reasons.Add("Device.UpdateMdmManaged");
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            reasons.Add("Device.UpdateManagementUnconfirmed");
        }
        return reasons;
    }

    /// <summary>更新の検索・ダウンロードを開始せずインストーラーの使用状態を読みます。</summary>
    /// <returns>WUAで確認できない場合はnullです。</returns>
    private static bool? Installing()
    {
        object? instance = null;
        try
        {
            Type? type = Type.GetTypeFromProgID("Microsoft.Update.Installer");
            if (type is null)
            {
                return null;
            }
            instance = Activator.CreateInstance(type);
            return type.InvokeMember("IsBusy", BindingFlags.GetProperty, null, instance, null, System.Globalization.CultureInfo.InvariantCulture) as bool?;
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException or MemberAccessException)
        {
            return null;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                _ = Marshal.FinalReleaseComObject(instance);
            }
        }
    }

    /// <summary>ドメイン参加状態を取得します。</summary>
    /// <returns>Win32結果です。</returns>
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint NetGetJoinInformation(string? server, out IntPtr name, out int status);

    /// <summary>NetAPIの領域を解放します。</summary>
    /// <returns>Win32結果です。</returns>
    [LibraryImport("netapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint NetApiBufferFree(IntPtr buffer);

    /// <summary>MDM登録状態だけを取得します。</summary>
    /// <returns>HRESULTです。</returns>
    [LibraryImport("MDMRegistration.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int IsDeviceRegisteredWithManagement([MarshalAs(UnmanagedType.Bool)] out bool registered, uint length, IntPtr upn);
}
