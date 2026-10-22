using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

/// <summary>成功した古い更新の実行物だけを整理し、操作記録と未確定更新を保持します。</summary>
public static class UpdateRetention
{
    /// <summary>直近3成功世代と24時間以内を保護し、旧Coreの退避・転送・helperを整理します。</summary>
    /// <returns>安全に確認できた古い更新の整理完了です。</returns>
    public static async Task TrimCoreAsync(UpdatesOptions options, string peerId, Guid currentOperation, CancellationToken cancellationToken = default)
    {
        UpdateLayout current = new(options, currentOperation);
        string operations = Path.Combine(current.StagingRoot, "operations");
        current.Validate(operations, true);
        if (!Directory.Exists(operations))
        {
            return;
        }
        List<(DateTimeOffset Time, UpdateLayout Layout)> completed = [];
        string guidFormat = "N";
        foreach (string directory in Directory.EnumerateDirectories(operations))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), guidFormat, out Guid id) || id == currentOperation)
            {
                continue;
            }
            try
            {
                UpdateLayout layout = new(options, id);
                LocalUpdateRecord? record = await new UpdateJournal(layout, peerId).ReadAsync(cancellationToken).ConfigureAwait(false);
                if (record is null || record.Job.OperationId != id || record.Result.OperationId != id
                    || record.Result.Phase != EUpdatePhase.Completed || record.Result.Outcome != EOutcome.Succeeded
                    || !string.Equals(record.Job.PeerId, peerId, StringComparison.Ordinal)
                    || !SamePath(record.Job.Options.InstallationRoot, current.InstallationRoot)
                    || !SamePath(record.Job.Options.StagingRoot, current.StagingRoot)
                    || !SamePath(record.Job.Options.BackupRoot, current.BackupRoot)
                    || ProcessOs.IsRunning(record.Writer) != false)
                {
                    continue;
                }
                completed.Add((File.GetLastWriteTimeUtc(layout.JournalPath), layout));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // 確認できない記録は削除対象にしません。
            }
        }
        foreach ((DateTimeOffset time, UpdateLayout layout) in completed.OrderByDescending(item => item.Time).Skip(3))
        {
            if (DateTimeOffset.UtcNow.AddDays(-1) <= time)
            {
                continue;
            }
            DeleteOwned(layout.BackupPath, path => layout.Validate(path, true), cancellationToken);
            DeleteOwned(layout.PayloadRoot, path => layout.Validate(path, true), cancellationToken);
            DeleteOwned(Path.Combine(layout.OperationRoot, "helper"), path => layout.Validate(path, true), cancellationToken);
        }
    }

    /// <summary>同じClientHost配置の成功済み旧世代だけを整理し、別配置や中断候補を保持します。</summary>
    /// <returns>元記録を残した旧実行物の整理完了です。</returns>
    public static async Task TrimClientAsync(ClientUpdateJob current, CancellationToken cancellationToken = default)
    {
        ClientUpdateFiles currentFiles = new(current);
        string operations = Path.GetDirectoryName(currentFiles.Operation)!;
        if (!Directory.Exists(operations))
        {
            return;
        }
        List<(DateTimeOffset Time, ClientUpdateFiles Files)> completed = [];
        string guidFormat = "N";
        foreach (string directory in Directory.EnumerateDirectories(operations))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), guidFormat, out Guid id) || id == current.OperationId)
            {
                continue;
            }
            try
            {
                ClientUpdateFiles candidate = new(current with { OperationId = id });
                candidate.Validate(candidate.JobPath);
                if (!File.Exists(candidate.JobPath))
                {
                    continue;
                }
                ClientUpdateJob? job = JsonSerializer.Deserialize<ClientUpdateJob>(await File.ReadAllTextAsync(candidate.JobPath, cancellationToken).ConfigureAwait(false), ContractJson.Options);
                if (job is null || job.OperationId != id || !SamePath(job.InstallationRoot, current.InstallationRoot)
                    || !SamePath(job.StateDirectory, current.StateDirectory))
                {
                    continue;
                }
                ClientUpdateProgress? result = await ClientUpdateFiles.ReadAsync(current.StateDirectory, id, cancellationToken).ConfigureAwait(false);
                string completedState = "Completed";
                if (result is { Completed: true, Outcome: EOutcome.Succeeded, Worker: not null }
                    && string.Equals(result.State, completedState, StringComparison.Ordinal)
                    && ProcessOs.IsRunning(result.Worker) == false)
                {
                    completed.Add((result.UpdatedAt, candidate));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // 不明な状態や別配置の内容には触れません。
            }
        }
        foreach ((DateTimeOffset time, ClientUpdateFiles files) in completed.OrderByDescending(item => item.Time).Skip(3))
        {
            if (DateTimeOffset.UtcNow.AddDays(-1) <= time)
            {
                continue;
            }
            DeleteOwned(files.Backup, files.Validate, cancellationToken);
            DeleteOwned(Path.Combine(files.Operation, "helper"), files.Validate, cancellationToken);
        }
    }

    /// <summary>InstallerからのWebクライアント更新も、確定成功とhelper終了を確認して整理します。</summary>
    /// <returns>未確定操作と記録を保護した旧資材の整理です。</returns>
    public static async Task TrimManagedClientAsync(string program, CancellationToken cancellationToken = default)
    {
        try
        {
            WebClientInstallation? installation = await WebClientCatalog.FindByProgramAsync(program, cancellationToken).ConfigureAwait(false);
            if (installation is not null)
            {
                await TrimManagedClientCoreAsync(installation, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            System.Diagnostics.Trace.TraceWarning("Web client update files could not be cleaned up: {0}", exception.Message);
        }
    }

    /// <summary>同じ導入の直近3成功世代と24時間以内を保護し、保存記録から資材の所有を確認します。</summary>
    /// <returns>確認できた成功世代だけの整理完了です。</returns>
    private static async Task TrimManagedClientCoreAsync(WebClientInstallation installation, CancellationToken cancellationToken = default)
    {
        string root = InstallationFiles.Root(installation.Root), program = WebClientCatalog.ProgramRoot(installation);
        FileAccessPolicy paths = new(new FileSystemOptions([root]));
        void Validate(string path) => paths.ValidatePath(path, true);
        string operations = Path.Combine(root, "update-staging", "operations");
        Validate(operations);
        if (!Directory.Exists(operations))
        {
            return;
        }
        List<(DateTimeOffset Time, string Directory, Guid Id)> completed = [];
        string ready = "ready", guidFormat = "N";
        foreach (string directory in Directory.EnumerateDirectories(operations))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), guidFormat, out Guid id))
            {
                continue;
            }
            try
            {
                Validate(directory);
                string jobPath = Path.Combine(directory, "job.json"), resultPath = Path.Combine(directory, "result.json"), writerPath = Path.Combine(directory, "writer.json"), startedPath = Path.Combine(directory, "started");
                foreach (string path in new[] { jobPath, resultPath, writerPath, startedPath })
                {
                    paths.ValidatePath(path, false);
                }
                WebClientUpdateJob? job = JsonSerializer.Deserialize<WebClientUpdateJob>(await File.ReadAllTextAsync(jobPath, cancellationToken).ConfigureAwait(false), ContractJson.Options);
                SetupResult? result = JsonSerializer.Deserialize<SetupResult>(await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false), ContractJson.Options);
                WebClientUpdateWriter? writer = JsonSerializer.Deserialize<WebClientUpdateWriter>(await File.ReadAllTextAsync(writerPath, cancellationToken).ConfigureAwait(false), ContractJson.Options);
                string started = await File.ReadAllTextAsync(startedPath, cancellationToken).ConfigureAwait(false);
                string payload = Path.Combine(directory, "payload");
                if (job is not { Installation: not null, Manifest: not null, PreviousProcess: not null } || job.OperationId != id
                    || job.Installation.InstallationId != installation.InstallationId || !SamePath(job.Installation.Root, root)
                    || !SamePath(WebClientCatalog.ProgramRoot(job.Installation), program) || !SamePath(job.PayloadRoot, payload)
                    || result is not { Finished: true, Outcome: EOutcome.Succeeded, WebClient: not null } || result.OperationId != id
                    || !string.Equals(result.Stage, ready, StringComparison.Ordinal) || !SamePath(result.Root, root)
                    || result.WebClient.InstallationId != installation.InstallationId || !SamePath(result.WebClient.Root, root)
                    || !SamePath(WebClientCatalog.ProgramRoot(result.WebClient), program)
                    || !string.Equals(result.WebClient.EntryPoint, job.Manifest.EntryPoint, StringComparison.Ordinal)
                    || writer is not { Process.ProcessId: > 0 } || writer.OperationId != id
                    || !string.Equals(writer.JobHash, ContractJson.Hash(job), StringComparison.Ordinal)
                    || !string.Equals(started, writer.JobHash, StringComparison.Ordinal) || ProcessOs.IsRunning(writer.Process) != false)
                {
                    continue;
                }
                completed.Add((File.GetLastWriteTimeUtc(resultPath), directory, id));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // 所有や終了を確認できない世代は、その資材も記録も残します。
            }
        }
        foreach ((DateTimeOffset time, string directory, Guid id) in completed.OrderByDescending(item => item.Time).Skip(3))
        {
            if (DateTimeOffset.UtcNow.AddDays(-1) <= time)
            {
                continue;
            }
            DeleteOwned(program + ".backup-" + id.ToString("N"), Validate, cancellationToken);
            DeleteOwned(program + ".candidate-" + id.ToString("N"), Validate, cancellationToken);
            DeleteOwned(Path.Combine(directory, "payload"), Validate, cancellationToken);
            DeleteOwned(Path.Combine(directory, "helper"), Validate, cancellationToken);
        }
    }

    /// <summary>境界・全リンクを削除前に確認し、利用中などの整理不能は更新失敗へ波及させません。</summary>
    private static void DeleteOwned(string root, Action<string> validate, CancellationToken cancellationToken = default)
    {
        try
        {
            validate(root);
            if (!Directory.Exists(root) || UpdateLayout.Contains(root, AppContext.BaseDirectory))
            {
                return;
            }
            List<string> directories = [root];
            List<string> files = [];
            for (int index = 0; index < directories.Count; index++)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directories[index]))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    validate(path);
                    if (Directory.Exists(path))
                    {
                        directories.Add(path);
                    }
                    else
                    {
                        files.Add(path);
                    }
                }
            }
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                validate(file);
                File.Delete(file);
            }
            foreach (string directory in directories.AsEnumerable().Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                validate(directory);
                Directory.Delete(directory, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 失敗した残りは次回再確認し、削除完了を成功記録へ偽装しません。
        }
    }

    /// <summary>保存済み入力のrootが現在の同じ配置を指すことを確認します。</summary>
    /// <returns>OSのパス規則で一致する場合だけtrueです。</returns>
    private static bool SamePath(string? first, string second)
    {
        StringComparison comparison = StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            comparison = StringComparison.OrdinalIgnoreCase;
        }
        return first is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), comparison);
    }
}
