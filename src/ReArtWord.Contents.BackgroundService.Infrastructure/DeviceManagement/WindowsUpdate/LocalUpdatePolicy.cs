using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WindowsUpdate;

/// <summary>一時的なレジストリ変更に留めず、ローカルGPOに更新設定を保存します。</summary>
[SupportedOSPlatform("windows")]
internal static class LocalUpdatePolicy
{
    private static readonly Guid classId = new("EA502722-A23D-11d1-A7D3-0000F87571E3");
    private static readonly Guid registryExtension = new("35378EAC-683F-11D2-A89A-00C04FBBCFA2");
    private static readonly Guid applicationId = new("28CC79DB-02D8-4579-9B6F-AD756BDA52CF");

    /// <summary>ローカルGPOと現在の有効値に同じ限定変更を適用します。</summary>
    internal static void Write(bool manualOnly, Action<string> completed)
    {
        Type type = Type.GetTypeFromCLSID(classId, true)!;
        object instance = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Device.UpdateChangeFailed");
        try
        {
            ILocalGroupPolicy policy = (ILocalGroupPolicy)instance;
            policy.OpenLocalMachine(1);
            policy.GetRegistryKey(2, out IntPtr key);
            using (SafeRegistryHandle handle = new(key, true))
            using (RegistryKey root = RegistryKey.FromHandle(handle))
            {
                Set(root, manualOnly);
            }
            policy.Save(true, true, in registryExtension, in applicationId);
            completed("updates.localPolicySaved");
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(instance);
        }
        Set(Registry.LocalMachine, manualOnly);
        completed("updates.effectivePolicySaved");
    }

    /// <summary>自動更新に優先する期限設定も、手動更新指定時だけ無効化します。</summary>
    internal static void Set(RegistryKey root, bool manualOnly)
    {
        using RegistryKey automatic = root.CreateSubKey(WindowsUpdateAccess.AutomaticPath);
        if (manualOnly)
        {
            using RegistryKey update = root.CreateSubKey(WindowsUpdateAccess.UpdatePath);
            foreach (string deadline in WindowsUpdateAccess.DeadlineNames)
            {
                update.SetValue(deadline, 0, RegistryValueKind.DWord);
            }
            update.Flush();
            automatic.SetValue("NoAutoUpdate", 1, RegistryValueKind.DWord);
        }
        else
        {
            automatic.DeleteValue("NoAutoUpdate", false);
        }
        automatic.Flush();
    }
}
