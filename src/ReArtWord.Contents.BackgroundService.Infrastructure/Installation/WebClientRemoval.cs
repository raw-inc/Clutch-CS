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
            File.Delete(Path.Combine(client.Root, "Open-Client.cmd"));
            File.Delete(Path.Combine(client.Root, "Start-Client.ps1"));
            File.Delete(Path.Combine(client.Root, "open-client.sh"));
            foreach (string legacyFile in legacyFiles)
            {
                File.Delete(legacyFile);
            }
            File.Delete(Path.Combine(program, LocalUpdateExecutor.ManifestFileName));
            string marker = Path.Combine(client.Root, WebClientCatalog.RecordName);
            if (File.Exists(marker))
            {
                File.Move(marker, Path.Combine(client.Root, WebClientCatalog.RemovedRecordName), true);
            }
            else
            {
                await InstallationFiles.WriteAsync(Path.Combine(client.Root, WebClientCatalog.RemovedRecordName), client, cancellationToken).ConfigureAwait(false);
            }
            if (!owned.KeepConfiguration)
            {
                InstallationFiles.DeleteDirectory(client.Root, "config");
                string state = WebClientStartup.StateDirectory(client);
                InstallationFiles.DeleteDirectory(Path.GetDirectoryName(state)!, Path.GetFileName(state));
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
