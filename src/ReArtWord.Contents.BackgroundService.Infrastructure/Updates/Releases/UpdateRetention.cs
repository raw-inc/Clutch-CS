using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;

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
