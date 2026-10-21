using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>Coreの自動起動と通信許可を登録します。</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsInstallation
{
    /// <summary>更新・切り戻しで起動登録と通信許可を現在の実行物へ揃えます。</summary>
    /// <returns>更新完了です。</returns>
    internal static async Task UpdateCoreAsync(InstallationRecord previous, InstallationRecord replacement, CancellationToken cancellationToken = default)
    {
        WindowsStartup startup = new();
        StartupRegistration registration = StartupRegistration.ForCore(previous);
        if (!startup.Owns(registration) && !registration.PerUser)
        {
            throw new IOException("The installation's Windows service is missing. Use Repair first.");
        }
        await WindowsFirewall.ConfigureAsync(replacement, false, cancellationToken: cancellationToken).ConfigureAwait(false);
        await startup.ReplaceAsync(registration, StartupRegistration.ForCore(replacement), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>サービスまたはサインイン時起動を登録します。</summary>
    /// <returns>登録完了です。</returns>
    internal static async Task RegisterAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        await WindowsFirewall.ConfigureAsync(record, false, cancellationToken: cancellationToken).ConfigureAwait(false);
        await new WindowsStartup().RegisterAsync(StartupRegistration.ForCore(record), cancellationToken).ConfigureAwait(false);
        InstallationCatalog.Register(record);
    }

    /// <summary>登録されたサービスを開始します。</summary>
    /// <returns>開始要求完了です。</returns>
    internal static Task StartAsync(InstallationRecord record, CancellationToken cancellationToken = default) => new WindowsStartup().StartAsync(StartupRegistration.ForCore(record), cancellationToken);

    /// <summary>所有するサービスの停止を確認します。</summary>
    /// <returns>停止完了です。</returns>
    internal static Task StopAsync(InstallationRecord record, CancellationToken cancellationToken = default) => new WindowsStartup().StopAsync(StartupRegistration.ForCore(record), cancellationToken);

    /// <summary>所有する起動登録と通信許可を解除します。</summary>
    /// <returns>解除完了です。</returns>
    internal static async Task UnregisterAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        await new WindowsStartup().UnregisterAsync(StartupRegistration.ForCore(record), cancellationToken).ConfigureAwait(false);
        await WindowsFirewall.ConfigureAsync(record, true, cancellationToken: cancellationToken).ConfigureAwait(false);
        InstallationCatalog.Remove(record);
    }
}