using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;

/// <summary>段階同期を単一SQLiteの原子的な保存境界と既存の排他へ接続します。</summary>
internal sealed class RecoveryRepository(SqliteRepository store, RoutedRepository? routed)
{
    private long previousRow;

    /// <summary>原子的に存在証明と現在値を保存できる構成だけを選びます。</summary>
    /// <returns>対応保存先、または従来同期を維持するnullです。</returns>
    internal static RecoveryRepository? Create(IDurableRepository repository) => repository switch
    {
        SqliteRepository sqlite => new(sqlite, null),
        RoutedRepository { HistoryStore: { } sqlite, JsonHistoryStore: null } routing => new(sqlite, routing),
        _ => null,
    };

    /// <summary>相手との通信待ちにDB排他を持ち越さず、同じ版の先行同期内容を固定します。</summary>
    /// <returns>本文未取得の存在証明を含む現在状態です。</returns>
    internal Task<RepositorySnapshot> ReadAsync(string peerId, DateTimeOffset now, CancellationToken cancellationToken = default)
        => AccessAsync(() => RecoverySnapshotRead.Read(store.HistoryPath, peerId, now, cancellationToken), cancellationToken);

    /// <summary>現在の稼働判定とは別の履歴復旧状態を読みます。</summary>
    /// <returns>未取得件数と直近の失敗です。</returns>
    internal Task<HistorySynchronizationStatus> StatusAsync(CancellationToken cancellationToken = default) => AccessAsync(() =>
    {
        using SqliteSession session = new(store.HistoryPath, write: false);
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT count(*),max(problem),(SELECT total FROM recovery_progress WHERE id=1) FROM recovery_history";
        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();
        string? problem = null;
        if (!reader.IsDBNull(1))
        {
            problem = reader.GetString(1);
        }
        return new HistorySynchronizationStatus(reader.GetInt64(0), problem, reader.GetInt64(2));
    }, cancellationToken);

    /// <summary>一回の背景取得を最大32操作へ制限し、未取得情報を再起動後も同じDBから選びます。</summary>
    /// <returns>優先する取得元と、小さい要求集合です。</returns>
    internal Task<(string? Peer, IReadOnlyList<RecoveryRecord> Receipts)> PendingAsync(CancellationToken cancellationToken = default) => AccessAsync(() =>
    {
        using SqliteSession session = new(store.HistoryPath, write: false);
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT source_peer,payload,hash,rowid FROM recovery_history ORDER BY rowid<=$previous,problem IS NOT NULL,rowid LIMIT 32";
        command.Parameters.AddWithValue("$previous", Volatile.Read(ref previousRow));
        using SqliteDataReader reader = command.ExecuteReader();
        List<RecoveryRecord> receipts = [];
        string? peer = null;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            peer ??= reader.GetString(0);
            string payload = reader.GetString(1);
            SqlitePayload.Verify(payload, reader.GetString(2));
            RecoveryRecord receipt = JsonSerializer.Deserialize<RecoveryRecord>(payload) ?? throw new InvalidDataException("背景取得する履歴が不正です。");
            RecoveryRecordLedger.Validate(receipt);
            receipts.Add(receipt);
            Volatile.Write(ref previousRow, reader.GetInt64(3));
        }
        return (peer, (IReadOnlyList<RecoveryRecord>)receipts);
    }, cancellationToken);

    /// <summary>背景通信で使う有限の本文だけを固定します。</summary>
    /// <returns>元ハッシュを検証済みの本文です。</returns>
    internal Task<IReadOnlyList<RecoveryRecordBody>> ReadBodiesAsync(IReadOnlyList<RecoveryRecord> receipts, CancellationToken cancellationToken = default)
        => AccessAsync(() => RecoveryHistoryTransfer.Read(store.HistoryPath, receipts, cancellationToken), cancellationToken);

    /// <summary>元の存在証明に一致した本文だけを、一バッチの原子的な取引で補完します。</summary>
    /// <returns>確定した履歴件数です。</returns>
    internal Task<int> FillAsync(IReadOnlyList<RecoveryRecordBody> receipts, CancellationToken cancellationToken = default)
        => AccessAsync(() => RecoveryHistoryTransfer.Fill(store.HistoryPath, receipts, cancellationToken), cancellationToken);

    /// <summary>用途振分けがある保存先でも通常の短い排他へ参加します。</summary>
    /// <returns>この一回の保存確認です。</returns>
    private Task<T> AccessAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return routed is null ? Task.FromResult(action())
                              : routed.HistoryAccessAsync(action, cancellationToken);
    }
}
