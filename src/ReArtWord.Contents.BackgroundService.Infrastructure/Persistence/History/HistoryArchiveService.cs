using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>実行経路と独立して少量の確定履歴を圧縮し、失敗を通常処理の再実行へ置き換えません。</summary>
public sealed class HistoryArchiveService : IDisposable
{
    private readonly SqliteRepository? sqlite;
    private readonly JsonFileRepository? json;
    private readonly RoutedRepository? routed;
    private readonly SemaphoreSlim working = new(1, 1);
    private string? problem;
    private bool reclaimPending = true;

    /// <summary>未使用のSQLiteファイルを新たに作らず、既存の用途割当から対象を取得します。</summary>
    public HistoryArchiveService(IDurableRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        routed = repository as RoutedRepository;
        sqlite = repository as SqliteRepository ?? routed?.HistoryStore;
        json = repository as JsonFileRepository ?? routed?.JsonHistoryStore;
    }

    /// <summary>共通保持の一単位として、基準点・圧縮・期限削除を順に確定します。</summary>
    /// <returns>今回削除した本文件数です。</returns>
    public async Task<int> MaintainAsync(HistoryArchiveOptions options, DataRetentionSettings retention, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(retention);
        options.Validate();
        retention.Validate();
        if (!options.Enabled)
        {
            return 0;
        }
        await working.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int jsonRemoved = 0;
            if (json is not null)
            {
                jsonRemoved = await AccessAsync(() => json.MaintainHistory(retention, now, options.BatchRecords), cancellationToken).ConfigureAwait(false);
            }
            if (sqlite is null)
            {
                Volatile.Write(ref problem, null);
                return jsonRemoved;
            }
            await AccessAsync(() => SqliteHistoryCheckpoints.Capture(sqlite.HistoryPath, retention, now, cancellationToken), cancellationToken).ConfigureAwait(false);
            HistoryRow[] rows = await AccessAsync(() => SqliteHistoryArchive.PrepareRetained(sqlite.HistoryPath, options, retention, now, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (rows.Length != 0)
            {
                (byte[] data, int length, string hash) = await Task.Run(() => HistoryBlockCodec.Encode(rows), cancellationToken).ConfigureAwait(false);
                int moved = await AccessAsync(() => SqliteHistoryArchive.CommitRetained(sqlite.HistoryPath, rows, data, length, hash, now, cancellationToken), cancellationToken).ConfigureAwait(false);
                reclaimPending |= moved != 0;
            }
            int removed = await AccessAsync(() => SqliteHistoryRetirement.DeleteOne(sqlite.HistoryPath, retention, now, cancellationToken), cancellationToken).ConfigureAwait(false);
            removed += await AccessAsync(() => SqliteHistoryHotDeletion.DeleteOne(sqlite.HistoryPath, retention, now, cancellationToken), cancellationToken).ConfigureAwait(false);
            await AccessAsync(() =>
            {
                SqliteHistoryCheckpoints.Maintain(sqlite.HistoryPath, retention, now);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            reclaimPending |= removed != 0;
            await ReclaimAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref problem, null);
            return removed + jsonRemoved;
        }
        finally
        {
            working.Release();
        }
    }

    /// <summary>背景保守を保留した理由を保存本文や例外文字列を含めずに表示します。</summary>
    public void Hold(string reason) => Volatile.Write(ref problem, reason);

    /// <summary>基準点の区間と実保存量を取得します。</summary>
    /// <returns>新しい順の基準点です。</returns>
    public Task<IReadOnlyList<HistoryCheckpointInfo>> ReadCheckpointsAsync(CancellationToken cancellationToken = default)
    {
        if (sqlite is null)
        {
            return AccessAsync<IReadOnlyList<HistoryCheckpointInfo>>(() => json?.HistoryCheckpoints() ?? [], cancellationToken);
        }
        return AccessAsync<IReadOnlyList<HistoryCheckpointInfo>>(() => SqliteHistoryCheckpoints.List(sqlite.HistoryPath).Concat(json?.HistoryCheckpoints() ?? []).OrderByDescending(checkpoint => checkpoint.CreatedAt).Take(100).ToArray(), cancellationToken);
    }

    /// <summary>同時に一処理だけを所有し、圧縮のCPU処理では保存先の排他を解放します。</summary>
    /// <returns>今回、本文を圧縮へ移した件数です。</returns>
    public async Task<int> ArchiveOnceAsync(HistoryArchiveOptions options, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (sqlite is null || !options.Enabled)
        {
            return 0;
        }
        await working.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HistoryRow[] rows = await AccessAsync(() => SqliteHistoryArchive.Prepare(sqlite.HistoryPath, options, now, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (rows.Length == 0)
            {
                await ReclaimAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref problem, null);
                return 0;
            }
            (byte[] data, int length, string hash) = await Task.Run(() => HistoryBlockCodec.Encode(rows), cancellationToken).ConfigureAwait(false);
            int moved = await AccessAsync(() => SqliteHistoryArchive.Commit(sqlite.HistoryPath, rows, data, length, hash, now, cancellationToken), cancellationToken).ConfigureAwait(false);
            reclaimPending |= moved != 0;
            await ReclaimAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref problem, null);
            return moved;
        }
        finally
        {
            working.Release();
        }
    }

    /// <summary>一回ごとの失敗を診断へ残し、次の周期にも所有された処理だけを試みます。</summary>
    /// <returns>Coreの停止までの有限処理の繰返しです。</returns>
    public async Task RunAsync(HistoryArchiveOptions options, Func<bool> idle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(idle);
        options.Validate();
        if (sqlite is null || !options.Enabled)
        {
            return;
        }
        int delaySeconds = 1;
        DateTimeOffset retryAt = DateTimeOffset.MinValue;
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!idle() || DateTimeOffset.UtcNow < retryAt)
            {
                continue;
            }
            try
            {
                await ArchiveOnceAsync(options, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                delaySeconds = 1;
            }
            catch (Exception error) when (error is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref problem, "History.ArchiveUnconfirmed");
                retryAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
                delaySeconds = Math.Min(60, delaySeconds * 2);
            }
        }
    }

    /// <summary>実ファイルと空き領域を区別し、圧縮だけで物理ファイルが縮んだとは表示しません。</summary>
    /// <returns>既存の履歴保存先についての小さい統計です。</returns>
    public Task<HistoryArchiveStatus> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        if (sqlite is null)
        {
            return AccessAsync(() => json?.HistoryStatus(Volatile.Read(ref problem)) ?? new HistoryArchiveStatus(false, 0, 0, 0, 0, 0, 0, 0, Volatile.Read(ref problem)), cancellationToken);
        }
        return AccessAsync(() => ReadStatus(sqlite.HistoryPath), cancellationToken);
    }

    /// <summary>所有した背景処理の終了後に、保守操作の排他を解放します。</summary>
    public void Dispose() => working.Dispose();

    /// <summary>DBの一括再構築を行わず、対応済みの空きページだけを有限回収します。</summary>
    /// <returns>今回の小さい回収単位の完了です。</returns>
    private async Task ReclaimAsync(CancellationToken cancellationToken = default)
    {
        if (reclaimPending && sqlite is not null)
        {
            reclaimPending = await AccessAsync(() => SqliteHistoryRestore.Reclaim(sqlite.HistoryPath, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>取込み途中の領域を別の媒体から参照しないようにします。</summary>
    /// <returns>実保存先が確認した処理結果です。</returns>
    private Task<T> AccessAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return routed is null ? Task.FromResult(operation())
                              : routed.HistoryAccessAsync(operation, cancellationToken);
    }

    /// <summary>索引件数とページ情報だけを読み、大きな本文を展開しません。</summary>
    /// <returns>この保存先の使用状況です。</returns>
    private HistoryArchiveStatus ReadStatus(string path)
    {
        using SqliteSession session = new(path, write: false);
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT COUNT(*) FROM history_candidates";
        long hot = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT COUNT(*) FROM history_index";
        long archived = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT COUNT(*) FROM records WHERE rowid>(SELECT last_rowid FROM history_scan WHERE id=1)";
        long unindexed = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT COUNT(*) FROM history_candidates WHERE reviewed_hash IS NOT NULL AND eligible_after=9223372036854775807";
        long protectedHot = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT COUNT(*) FROM history_legacy_formats";
        long legacy = (long)command.ExecuteScalar()!;
        command.CommandText = """
            SELECT COUNT(*) FROM history_candidates c JOIN records r ON r.kind=c.kind AND r.key=c.key
            WHERE c.reviewed_hash IS NULL OR r.rowid>(SELECT last_rowid FROM history_legacy_scan WHERE id=1)
            """;
        long unreviewed = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT COUNT(*),COALESCE(SUM(length(data)),0),COALESCE(SUM(raw_length),0) FROM history_blocks";
        long blocks, compressed, expanded;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            reader.Read();
            blocks = reader.GetInt64(0);
            compressed = reader.GetInt64(1);
            expanded = reader.GetInt64(2);
        }
        command.CommandText = "PRAGMA page_size";
        long page = (long)command.ExecuteScalar()!;
        command.CommandText = "PRAGMA page_count";
        long total = (long)command.ExecuteScalar()! * page;
        command.CommandText = "PRAGMA freelist_count";
        long free = (long)command.ExecuteScalar()! * page;
        return new(true, hot, archived, blocks, compressed, expanded, total, free, Volatile.Read(ref problem), unindexed, protectedHot, legacy, unreviewed);
    }
}
