using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>明示選択したWebクライアントだけを削除し、Coreは操作しません。</summary>
public sealed class WebClientRemoval(IInstallationPlatform platform)
{
    /// <summary>所有を確認した配布fileと起動入口だけを削除します。</summary>
    /// <returns>保存された削除結果です。</returns>
    public async Task<SetupResult> ExecuteAsync(WebClientRemovalJob job, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        WebClientRemovalJob owned = ContractJson.Copy(job);
        string journalRoot = InstallationFiles.Root(directory);
        Directory.CreateDirectory(journalRoot);
        SetupOperationJournal journal = new(owned.OperationId, journalRoot, owned.Installation.Root);
        SetupResult? previous = await journal.BeginAsync(owned, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            return previous;
        }
        SetupProgressJournal progress = new(ESetupAction.Uninstall, journalRoot, true);
        try
        {
            ArgumentOutOfRangeException.ThrowIfEqual(owned.OperationId, Guid.Empty);
            if (UpdateLayout.Contains(owned.Installation.Root, journalRoot))
            {
                throw new InvalidDataException("The operation journal must be outside the web client.");
            }
            await progress.BeginAsync("validate", cancellationToken).ConfigureAwait(false);
            using FileStream clientLock = WebClientCatalog.AcquireLock(owned.Installation.Root);
            WebClientInstallation client = await WebClientCatalog.ReadAsync(owned.Installation.Root, false, cancellationToken).ConfigureAwait(false);
            if (client != owned.Installation)
            {
                throw new InvalidDataException("The web client installation has changed. Select it again.");
            }
            string program = WebClientCatalog.ProgramRoot(client);
            ReleaseManifest manifest = await LocalUpdateExecutor.ReadManifestAsync(program, cancellationToken).ConfigureAwait(false);
            string[] legacyFiles = await LegacyFilesAsync(client, owned.KeepConfiguration, cancellationToken).ConfigureAwait(false);
            await progress.BeginAsync("stop", cancellationToken).ConfigureAwait(false);
            await platform.StopWebClientAsync(client, cancellationToken).ConfigureAwait(false);
            await progress.BeginAsync("unregister", cancellationToken).ConfigureAwait(false);
            await platform.UnregisterWebClientAsync(client, cancellationToken).ConfigureAwait(false);
            await progress.BeginAsync("remove", cancellationToken).ConfigureAwait(false);
            foreach (ReleaseFile file in manifest.Files)
            {
                await InstallationFiles.DeleteFileAsync(program, file.Path.Replace('/', Path.DirectorySeparatorChar), cancellationToken).ConfigureAwait(false);
            }
            await InstallationFiles.DeleteFileAsync(client.Root, "Open-Client.cmd", cancellationToken).ConfigureAwait(false);
            await InstallationFiles.DeleteFileAsync(client.Root, "Start-Client.ps1", cancellationToken).ConfigureAwait(false);
            await InstallationFiles.DeleteFileAsync(client.Root, "open-client.sh", cancellationToken).ConfigureAwait(false);
            foreach (string legacyFile in legacyFiles)
            {
                string parent = Path.GetDirectoryName(client.Root)!;
                await InstallationFiles.DeleteFileAsync(parent, Path.GetRelativePath(parent, legacyFile), cancellationToken).ConfigureAwait(false);
            }
            await InstallationFiles.DeleteFileAsync(program, LocalUpdateExecutor.ManifestFileName, cancellationToken).ConfigureAwait(false);
            if (!owned.KeepConfiguration)
            {
                await InstallationFiles.DeleteDirectoryAsync(client.Root, "config", cancellationToken).ConfigureAwait(false);
                string state = WebClientStartup.StateDirectory(client);
                await InstallationFiles.DeleteDirectoryAsync(Path.GetDirectoryName(state)!, Path.GetFileName(state), cancellationToken).ConfigureAwait(false);
            }

            // 設定・状態の削除も完了してから、導入記録を削除済みへ切り替えます。
            string marker = Path.Combine(client.Root, WebClientCatalog.RecordName);
            if (File.Exists(marker))
            {
                await InstallationFiles.MoveFileAsync(client.Root, WebClientCatalog.RecordName, WebClientCatalog.RemovedRecordName, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await InstallationFiles.WriteAsync(Path.Combine(client.Root, WebClientCatalog.RemovedRecordName), client, cancellationToken).ConfigureAwait(false);
            }
            await progress.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return await journal.CompleteAsync(new(owned.OperationId, EOutcome.Succeeded, "uninstalled", "The web client was removed. Core services were not changed.", client.Root), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or System.Text.Json.JsonException)
        {
            EOutcome outcome = EOutcome.Failed;
            if (exception is OperationCanceledException)
            {
                outcome = EOutcome.Unknown;
            }
            return await journal.CompleteAsync(new(owned.OperationId, outcome, progress.Current.Stage, exception.Message, owned.Installation.Root), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>旧配置の起動入口と表示設定は、親の所有記録が一致する場合だけ扱います。</summary>
    /// <returns>明示削除する旧配置のfileです。</returns>
    private static async Task<string[]> LegacyFilesAsync(WebClientInstallation client, bool keepConfiguration, CancellationToken cancellationToken = default)
    {
        string parent = Path.GetDirectoryName(client.Root)!;
        string expected = Path.Combine(parent, "client");
        if (!string.Equals(client.Root, expected, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
        string recordPath = Path.Combine(parent, InstallationFiles.RecordName);
        if (!File.Exists(recordPath))
        {
            recordPath = Path.Combine(parent, "uninstalled.json");
        }
        if (!File.Exists(recordPath))
        {
            return [];
        }
        InstallationRecord? record = System.Text.Json.JsonSerializer.Deserialize<InstallationRecord>(await File.ReadAllTextAsync(recordPath, cancellationToken).ConfigureAwait(false), ContractJson.Options);
        if (record is null || record.InstallationId != client.InstallationId || record.Settings.WebClient is not null
            || !string.Equals(InstallationFiles.Root(record.Settings.Root), parent, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
        List<string> names = ["Open-Client.cmd", "open-client.sh"];
        if (!keepConfiguration)
        {
            foreach (string name in new[] { "appearance.json", "icon.png", "icon.ico", "favicon.png", "favicon.ico" })
            {
                names.Add(Path.Combine("config", name));
            }
        }
        Runtime.Files.FileAccessPolicy policy = new(new Contracts.Files.FileSystemOptions([parent]));
        return names.Select(name => policy.ResolvePath(Path.Combine(parent, name), true)).ToArray();
    }
}
