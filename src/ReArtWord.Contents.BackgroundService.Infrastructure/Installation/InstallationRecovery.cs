using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>修復に使う検証済み配布物を現在の導入版へ揃えます。</summary>
public static class InstallationRecovery
{
    /// <summary>起動直前に管理配置の実行物とWindows通信許可を現在のmanifestへ揃えます。</summary>
    /// <returns>手動配置以外の起動登録と所有記録の更新です。</returns>
    public static async Task SynchronizeCoreAsync(string coreRoot, ReleaseManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string canonical = InstallationFiles.Root(coreRoot);
        string root = Path.GetDirectoryName(canonical)!;
        if (!File.Exists(Path.Combine(root, InstallationFiles.RecordName)))
        {
            return;
        }
        InstallationRecord previous = await InstallationFiles.ReadAsync(root, cancellationToken).ConfigureAwait(false);
        string expected = Path.Combine(root, "core");
        if (!string.Equals(expected, canonical, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update folder does not belong to this installation.");
        }
        InstallationRecord replacement = previous with { CoreEntryPoint = manifest.EntryPoint };
        if (OperatingSystem.IsWindows())
        {
            await WindowsInstallation.UpdateCoreAsync(previous, replacement, cancellationToken).ConfigureAwait(false);
        }
        await InstallationFiles.WriteAsync(Path.Combine(root, InstallationFiles.RecordName), replacement, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GUI管理配置なら更新後のCoreを修復元へ保存します。</summary>
    /// <returns>保存完了です。従来の手動配置は対象外です。</returns>
    public static async Task RefreshCoreAsync(string coreRoot, ReleaseManifest manifest, CancellationToken cancellationToken = default)
    {
        string canonical = InstallationFiles.Root(coreRoot);
        string root = Path.GetDirectoryName(canonical)!;
        if (!File.Exists(Path.Combine(root, InstallationFiles.RecordName)))
        {
            return;
        }
        _ = await InstallationFiles.ReadAsync(root, cancellationToken).ConfigureAwait(false);
        string ownedCore = Path.Combine(root, "core");
        if (!string.Equals(ownedCore, canonical, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update folder does not belong to this installation.");
        }
        await ReplaceAsync(root, "core", coreRoot, manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>入替完了したcomponentの配布物を修復用に保持します。</summary>
    /// <returns>検証済み複製の保存完了です。</returns>
    public static async Task ReplaceAsync(string root, string component, string source, ReleaseManifest manifest, CancellationToken cancellationToken = default)
    {
        string recovery = InstallationFiles.Root(Path.Combine(root, "recovery"));
        string destination = InstallationFiles.Root(Path.Combine(recovery, component));
        if (string.Equals(InstallationFiles.Root(source), destination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        string temporary = destination + "-" + Guid.NewGuid().ToString("N");
        string previous = temporary + "-previous";
        await ReleaseVerifier.CopyAsync(manifest, source, temporary, cancellationToken).ConfigureAwait(false);
        await LocalUpdateExecutor.WriteManifestAsync(manifest, temporary, cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(destination))
        {
            await InstallationFiles.MoveDirectoryAsync(recovery, destination, previous, cancellationToken).ConfigureAwait(false);
        }
        await InstallationFiles.MoveDirectoryAsync(recovery, temporary, destination, cancellationToken).ConfigureAwait(false);
        InstallationFiles.DeleteDirectory(recovery, Path.GetFileName(previous));
    }

    /// <summary>保存元が現在版と異なるとき、暗黙の旧版復帰を拒否します。</summary>
    /// <returns>版一致の確認完了です。</returns>
    public static async Task ValidateVersionAsync(InstallationRecord record, string packageRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ReleaseManifest candidate = await LocalUpdateExecutor.ReadManifestAsync(Path.Combine(packageRoot, "core"), cancellationToken).ConfigureAwait(false);
        string installed = Path.Combine(record.Settings.Root, "core");
        string manifestPath = Path.Combine(installed, LocalUpdateExecutor.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return;
        }
        ReleaseManifest current = await LocalUpdateExecutor.ReadManifestAsync(installed, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(ContractJson.Hash(current), ContractJson.Hash(candidate), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Repair files do not match the installed version. Use the installer for the installed version, or Update to change versions.");
        }
    }
}
