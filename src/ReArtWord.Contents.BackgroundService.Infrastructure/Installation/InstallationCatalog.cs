using Microsoft.Win32;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>停止した配置もGUIから見つけられるOS別索引です。</summary>
public static class InstallationCatalog
{
    private static string RegistryPath => @"Software\ReArtWord\Clutch-CS\Installations";

    /// <summary>導入先を現在の権限範囲へ記録します。</summary>
    public static void Register(InstallationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (OperatingSystem.IsWindows())
        {
            RegistryKey hive = Registry.LocalMachine;
            if (record.Settings.PerUser)
            {
                hive = Registry.CurrentUser;
            }
            using RegistryKey key = hive.CreateSubKey(RegistryPath);
            key.SetValue(record.InstallationId.ToString("N"), record.Settings.Root);
        }
    }

    /// <summary>削除した配置だけを索引から除きます。</summary>
    public static void Remove(InstallationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (OperatingSystem.IsWindows())
        {
            RegistryKey hive = Registry.LocalMachine;
            if (record.Settings.PerUser)
            {
                hive = Registry.CurrentUser;
            }
            using RegistryKey? key = hive.OpenSubKey(RegistryPath, true);
            key?.DeleteValue(record.InstallationId.ToString("N"), false);
        }
    }

    /// <summary>ユーザー・マシン両方の配置先を照会します。</summary>
    /// <returns>重複のない導入先です。</returns>
    public static IReadOnlyList<string> GetRoots()
    {
        List<string> roots = [];
        if (OperatingSystem.IsWindows())
        {
            foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using RegistryKey? key = hive.OpenSubKey(RegistryPath);
                if (key is null)
                {
                    continue;
                }
                foreach (string name in key.GetValueNames())
                {
                    if (key.GetValue(name) is string path)
                    {
                        roots.Add(path);
                    }
                }
            }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
