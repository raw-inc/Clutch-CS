using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Updater;

/// <summary>スクリプトからのサービス導入にも GUI と同じファイル保護を適用します。</summary>
internal static class InstallSecurity
{
    /// <summary>明示した配置を保護し、外部の設定ファイルは変更せずに検証します。</summary>
    /// <returns>保護を確認した場合は0です。</returns>
    internal static async Task<int> RunAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows service permissions require Windows.");
        }
        JsonElement request = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options);
        string root = request.GetProperty("installationRoot").GetString() ?? throw new InvalidDataException("The installation folder is missing.");
        string configuration = request.GetProperty("configurationPath").GetString() ?? throw new InvalidDataException("The configuration path is missing.");
        WindowsInstallationSecurity.ValidateFile(configuration);
        WindowsInstallationSecurity.ProtectTree(root, perUser: false);
        WindowsInstallationSecurity.ValidateTree(root, perUser: false);
        await ReleaseVerifier.VerifyAsync(await LocalUpdateExecutor.ReadManifestAsync(root, cancellationToken).ConfigureAwait(false), root, cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
