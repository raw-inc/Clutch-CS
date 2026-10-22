using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>同じ利用者の独立Updaterが、明示されたWebクライアントを更新・復帰確認します。</summary>
public sealed class WebClientUpdateExecutor(IInstallationPlatform platform)
{
    /// <summary>設定を入替範囲から除き、同じ操作を二度実行しません。</summary>
    /// <returns>起動確認済みの更新結果または復帰結果です。</returns>
    public async Task<SetupResult> ExecuteAsync(WebClientUpdateJob job, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        WebClientUpdateJob owned = ContractJson.Copy(job);
        WebClientInstallation previous = owned.Installation;
        string workspace = InstallationFiles.Root(directory);
        Directory.CreateDirectory(workspace);
        SetupOperationJournal journal = new(owned.OperationId, workspace, previous.Root);
        SetupResult? saved = await journal.BeginAsync(owned, cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            return saved;
        }
        SetupResult result;
        try
        {
            using FileStream clientLock = WebClientCatalog.AcquireLock(previous.Root);
            await InstallationFiles.WriteAsync(Path.Combine(workspace, "writer.json"),
                new WebClientUpdateWriter(owned.OperationId, ContractJson.Hash(owned), Processes.ProcessOs.CurrentIdentity()), cancellationToken).ConfigureAwait(false);
            result = await UpdateAsync(owned, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            result = new(owned.OperationId, EOutcome.NotExecuted, "validate", exception.Message, previous.Root);
        }
        return await journal.CompleteAsync(result, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>全ファイルの検証後だけ停止し、起動失敗時は旧版へ復帰します。</summary>
    /// <returns>入替と実起動の結果です。</returns>
    private async Task<SetupResult> UpdateAsync(WebClientUpdateJob job, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(job.OperationId, Guid.Empty);
        WebClientInstallation previous = await WebClientCatalog.ReadAsync(job.Installation.Root, cancellationToken).ConfigureAwait(false);
        if (previous != job.Installation || !previous.AutomaticStartup || previous.ProgramDirectory is null)
        {
            throw new InvalidDataException("Select the current managed web client installation.");
        }
        WebClientUpdateInfo running = await platform.InspectWebClientAsync(previous, cancellationToken).ConfigureAwait(false);
        if (running.Health.Process != job.PreviousProcess)
        {
            throw new InvalidDataException("The web client process changed. Select it again.");
        }
        ClientUpdateExecutor.ValidateRelease(job.Manifest);
        string program = WebClientCatalog.ProgramRoot(previous);
        if (UpdateLayout.Contains(program, AppContext.BaseDirectory))
        {
            throw new InvalidDataException("The updater must run outside the web client program directory.");
        }
        string pending = program + ".candidate-" + job.OperationId.ToString("N");
        string backup = program + ".backup-" + job.OperationId.ToString("N");
        FileAccessPolicy paths = new(new FileSystemOptions([program, pending, backup]));
        void Validate(string path) => paths.ValidatePath(path, true);
        Validate(program);
        Validate(pending);
        Validate(backup);
        if (Directory.Exists(pending) || Directory.Exists(backup))
        {
            throw new IOException("This update already has preparation files. Inspect its saved result.");
        }
        ReleaseManifest original = await LocalUpdateExecutor.ReadManifestAsync(program, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(original.EntryPoint, previous.EntryPoint, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The installed web client entry point changed.");
        }
        await ReleaseVerifier.VerifyAsync(original, program, cancellationToken).ConfigureAwait(false);
        await ReleaseVerifier.CopyAsync(job.Manifest, job.PayloadRoot, pending, cancellationToken).ConfigureAwait(false);
        await LocalUpdateExecutor.WriteManifestAsync(job.Manifest, pending, cancellationToken).ConfigureAwait(false);
        if ((await platform.InspectWebClientAsync(previous, cancellationToken).ConfigureAwait(false)).Health.Process != job.PreviousProcess)
        {
            throw new InvalidDataException("The web client process changed during preparation. Select it again.");
        }
        WebClientInstallation updated = previous with { EntryPoint = job.Manifest.EntryPoint };
        bool stopped = false;
        try
        {
            await platform.StopWebClientAsync(previous, cancellationToken).ConfigureAwait(false);
            stopped = true;
            await UpdateDirectorySwitch.MoveAsync(program, backup, Validate, cancellationToken).ConfigureAwait(false);
            await UpdateDirectorySwitch.MoveAsync(pending, program, Validate, cancellationToken).ConfigureAwait(false);
            await StartAsync(updated, cancellationToken).ConfigureAwait(false);
            WebClientUpdateInfo ready = await platform.InspectWebClientAsync(updated, cancellationToken).ConfigureAwait(false);
            if (ready.Health.Process == job.PreviousProcess || !string.Equals(ready.Health.BuildVersion, job.Manifest.ExpectedBuildVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The updated web client did not confirm the expected build.");
            }
            return new(job.OperationId, EOutcome.Succeeded, "ready", "The web client update is ready.", previous.Root, WebClient: updated);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            if (!stopped)
            {
                return new(job.OperationId, EOutcome.Unknown, "stop", exception.Message, previous.Root);
            }
            try
            {
                if (Directory.Exists(backup))
                {
                    await platform.StopWebClientAsync(updated, CancellationToken.None).ConfigureAwait(false);
                    if (Directory.Exists(program))
                    {
                        await UpdateDirectorySwitch.MoveAsync(program, pending, Validate, CancellationToken.None).ConfigureAwait(false);
                    }
                    await UpdateDirectorySwitch.MoveAsync(backup, program, Validate, CancellationToken.None).ConfigureAwait(false);
                }
                await ReleaseVerifier.VerifyAsync(original, program, CancellationToken.None).ConfigureAwait(false);
                await StartAsync(previous, CancellationToken.None).ConfigureAwait(false);
                WebClientUpdateInfo restored = await platform.InspectWebClientAsync(previous, CancellationToken.None).ConfigureAwait(false);
                if (!string.Equals(restored.Health.BuildVersion, original.ExpectedBuildVersion, StringComparison.Ordinal))
                {
                    return new(job.OperationId, EOutcome.Unknown, "restore", "The previous web client build could not be confirmed.", previous.Root);
                }
                return new(job.OperationId, EOutcome.Failed, "restored", exception.Message, previous.Root, WebClient: previous);
            }
            catch (Exception restoration) when (restoration is IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                return new(job.OperationId, EOutcome.Unknown, "restore", exception.Message + " / " + restoration.Message, previous.Root);
            }
        }
    }

    /// <summary>実行ファイル名の変更を導入記録と起動登録へ反映し、設定は保持します。</summary>
    /// <returns>登録と起動確認です。</returns>
    private async Task StartAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        await InstallationFiles.WriteAsync(Path.Combine(client.Root, WebClientCatalog.RecordName), client, cancellationToken).ConfigureAwait(false);
        await ClientLaunchFiles.WriteClientAsync(client, cancellationToken).ConfigureAwait(false);
        await platform.StartWebClientAsync(client, cancellationToken).ConfigureAwait(false);
    }
}
