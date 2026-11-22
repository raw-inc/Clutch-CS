using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;

/// <summary>全体更新の固定要求と結果を、一ファイルの原子的置換で保存します。</summary>
internal sealed class FleetUpdateStore
{
    private readonly string directory;
    private readonly string stateDirectory;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DisplayEntry> display = new(StringComparer.Ordinal);

    /// <summary>ClientHostの永続領域だけを使います。</summary>
    internal FleetUpdateStore(string stateDirectory)
    {
        this.stateDirectory = stateDirectory;
        directory = Path.Combine(stateDirectory, "fleet-updates");
        Directory.CreateDirectory(directory);
    }

    /// <summary>通知前に番号をまとめて確保し、ClientHostが中断しても発行済み番号を再利用しません。</summary>
    /// <returns>同じ保存先を使う発行元のIDと、今回使用できる通知番号の範囲です。</returns>
    internal async Task<(Guid PublisherId, long First, long Last)> ReservePublicationAsync(CancellationToken cancellationToken = default)
    {
        // ClientHostは起動中、状態ディレクトリを排他所有します。進捗保存と独立したファイルに上限を先に確定します。
        string path = Path.Combine(stateDirectory, "fleet-publication.json");
        Publication saved;
        try
        {
            saved = await UpdateProgressFile.ReadAsync<Publication>(path, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The fleet publication sequence is missing.");
        }
        catch (FileNotFoundException)
        {
            saved = new(Guid.NewGuid(), 0);
        }
        if (saved.PublisherId == Guid.Empty || saved.Last < 0 || long.MaxValue - 65536 < saved.Last)
        {
            throw new InvalidDataException("Invalid fleet publication sequence.");
        }
        Publication next = saved with { Last = saved.Last + 65536 };
        await UpdateProgressFile.WriteAsync(path, next, cancellationToken).ConfigureAwait(false);
        return (next.PublisherId, saved.Last + 1, next.Last);
    }

    /// <summary>表示通知に予約済みの番号です。更新操作の実行状態には使いません。</summary>
    private sealed record Publication(Guid PublisherId, long Last);

    /// <summary>起動時に未完了操作を未確認とし、停止・入替を暗黙再送しません。</summary>
    /// <returns>中断の記録です。</returns>
    internal async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            FleetUpdateStatus status = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (!status.Completed && status.Client?.Worker is null)
            {
                await SaveAsync(status with { Completed = true, Outcome = EOutcome.Unknown, UpdatedAt = DateTimeOffset.UtcNow,
                    Reason = "ClientHost stopped during this update. Inspect the recorded operation IDs before starting another update." }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>元IDの保存結果を読みます。</summary>
    /// <returns>保存済み要求・結果、または未受付です。</returns>
    internal Task<FleetUpdateStatus?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(directory, id.ToString("N") + ".json");
        return ReadOptionalAsync(path, cancellationToken);
    }

    /// <summary>完了履歴を新しい順で最大20件返します。</summary>
    /// <returns>画面が再接続しても同じ進捗を返す一覧です。</returns>
    internal async Task<IReadOnlyList<FleetUpdateStatus>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<FleetUpdateStatus> statuses = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(20))
        {
            statuses.Add(await ReadFileAsync(path, cancellationToken).ConfigureAwait(false));
        }
        return statuses;
    }

    /// <summary>変更のないマニフェストを再読込みせず、画面と通知には小さい進捗だけを返します。</summary>
    /// <returns>直近20件の表示用スナップショットです。</returns>
    internal async Task<IReadOnlyList<FleetUpdateView>> ListViewsAsync(CancellationToken cancellationToken = default)
    {
        string[] files = Directory.EnumerateFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(20).ToArray();
        List<FleetUpdateView> values = [];
        string updating = "Updating", launching = "Launching";
        foreach (string path in files)
        {
            FileInfo file = new(path);
            DateTimeOffset writtenAt = new(File.GetLastWriteTimeUtc(path));
            if (!display.TryGetValue(path, out DisplayEntry? known) || known.WrittenAt != writtenAt || known.Length != file.Length
                || string.Equals(known.Value.Client?.State, updating, StringComparison.Ordinal) || string.Equals(known.Value.Client?.State, launching, StringComparison.Ordinal))
            {
                known = new(writtenAt, file.Length, FleetUpdateView.From(await ReadFileAsync(path, cancellationToken).ConfigureAwait(false)));
                display[path] = known;
            }
            values.Add(known.Value);
        }
        foreach (string expired in display.Keys.Where(path => !files.Contains(path, StringComparer.Ordinal)))
        {
            display.TryRemove(expired, out _);
        }
        return values;
    }

    /// <summary>進捗を確定してから公開し、元IDの再受付を防ぐ記録はディスクへ保持します。</summary>
    /// <returns>固定入力と進捗の保存です。</returns>
    internal async Task SaveAsync(FleetUpdateStatus status, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(directory, status.Request.OperationId.ToString("N") + ".json");
        await UpdateProgressFile.WriteAsync(path, status, cancellationToken).ConfigureAwait(false);
        display.TryRemove(path, out _);
    }

    /// <summary>未受付と不正ファイルを区別します。</summary>
    /// <returns>保存された操作です。</returns>
    private async Task<FleetUpdateStatus?> ReadOptionalAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        return await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存時と同じ型とJSON契約を使用します。</summary>
    /// <returns>検証可能な固定進捗です。</returns>
    private async Task<FleetUpdateStatus> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        FleetUpdateStatus status = await UpdateProgressFile.ReadAsync<FleetUpdateStatus>(path, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The saved release update could not be read.");
        string updating = "Updating", launching = "Launching";
        if (status.Client is not null && (string.Equals(status.Client.State, updating, StringComparison.Ordinal) || string.Equals(status.Client.State, launching, StringComparison.Ordinal)))
        {
            ClientUpdateProgress? progress = await ClientUpdateFiles.ReadAsync(stateDirectory, status.Request.OperationId, cancellationToken).ConfigureAwait(false);
            if (progress is null)
            {
                if (string.Equals(status.Client.State, updating, StringComparison.Ordinal))
                {
                    return status with { Completed = true, Outcome = EOutcome.Unknown,
                        Reason = "The accepted ClientHost updater result is missing. Inspect the saved operation." };
                }
                return status;
            }
            EOutcome outcome = progress.Outcome;
            if (progress.Completed && outcome is not (EOutcome.Succeeded or EOutcome.Unknown))
            {
                outcome = EOutcome.Failed;
            }
            return status with { Client = progress, Completed = progress.Completed, Outcome = outcome, UpdatedAt = progress.UpdatedAt, Reason = progress.Reason };
        }
        return status;
    }

    /// <summary>完全な配布要求は保持せず、変更検知と表示だけに使います。</summary>
    private sealed record DisplayEntry(DateTimeOffset WrittenAt, long Length, FleetUpdateView Value);
}
