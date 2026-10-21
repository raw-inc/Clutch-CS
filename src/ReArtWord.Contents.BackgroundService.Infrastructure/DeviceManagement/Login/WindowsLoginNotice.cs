using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Login;

/// <summary>現在適用中のログインメッセージを、新旧の保存先で一貫して扱います。</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsLoginNotice
{
    private static IReadOnlyList<string> ValueNames
    {
        get;
    } = ["legalnoticecaption", "legalnoticetext"];

    /// <summary>空白だけのメッセージも、自動ログインを妨げる値として検出します。</summary>
    /// <returns>タイトルまたは本文が保存されているかです。</returns>
    internal static bool Exists(RegistryKey? system, RegistryKey? winlogon) => ValueNames.Any(name =>
        !string.IsNullOrEmpty(system?.GetValue(name) as string) || !string.IsNullOrEmpty(winlogon?.GetValue(name) as string));

    /// <summary>他のログイン設定や管理元のポリシーを変更せず、現在値だけを削除します。</summary>
    internal static void Clear(RegistryKey? system, RegistryKey? winlogon)
    {
        foreach (RegistryKey? key in new[] { system, winlogon })
        {
            if (key is null)
            {
                continue;
            }
            foreach (string name in ValueNames)
            {
                key.DeleteValue(name, false);
            }
            key.Flush();
        }
    }
}
