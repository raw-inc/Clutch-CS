using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>変更されていない準備の本文を保持せず、検証済みの指紋と共有分類だけを短時間再利用します。</summary>
internal sealed class SqlitePreparationCache(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Lock validation = new();
    private readonly Dictionary<Guid, Verified> entries = [];

    /// <summary>内容版の変化と削除を毎回確認し、同じ大きな準備本文の再復元を省きます。</summary>
    /// <returns>現在の準備の検証済み指紋です。</returns>
    internal string? Read(string path, Guid operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT v.source_hash FROM records r INDEXED BY sqlite_autoindex_records_1 LEFT JOIN preparation_versions v ON v.operation=r.key WHERE r.kind='prepared' AND r.key=$id";
        command.Parameters.AddWithValue("$id", operationId.ToString("N"));
        string hash;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                using (gate.EnterScope())
                {
                    entries.Remove(operationId);
                }
                return null;
            }
            if (reader.IsDBNull(0))
            {
                throw new InvalidDataException("保存準備の内容版索引がありません。");
            }
            hash = reader.GetString(0);
        }
        return Read(session, operationId, hash, cancellationToken).Fingerprint;
    }

    /// <summary>共有版の確認では、全準備の値を毎回復元せず識別子と内容版を照合します。</summary>
    /// <returns>同じ読取り取引で確認した共有準備のIDです。</returns>
    internal Guid[] SharedIds(SqliteSession session, CancellationToken cancellationToken)
    {
        List<(Guid Id, string Hash)> observed = [];
        using (SqliteCommand command = session.HistoryCommand())
        {
            command.CommandText = "SELECT r.key,v.source_hash FROM records r INDEXED BY sqlite_autoindex_records_1 LEFT JOIN preparation_versions v ON v.operation=r.key WHERE r.kind='prepared' ORDER BY r.key";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.IsDBNull(1))
                {
                    throw new InvalidDataException("保存準備の内容版索引がありません。");
                }
                observed.Add((SqlitePreparations.OperationId(reader.GetString(0)), reader.GetString(1)));
            }
        }
        HashSet<Guid> present = observed.Select(item => item.Id).ToHashSet();
        using (gate.EnterScope())
        {
            foreach (Guid removed in entries.Keys.Where(id => !present.Contains(id)).ToArray())
            {
                entries.Remove(removed);
            }
        }
        return observed.Where(item => Read(session, item.Id, item.Hash, cancellationToken).Shared).Select(item => item.Id).Order().ToArray();
    }

    /// <summary>新規・内容変更・30秒経過では必ず元本文を検査し、SQL外の破損検出も維持します。</summary>
    /// <returns>大きい本文への参照を持たない確認結果です。</returns>
    private Verified Read(SqliteSession session, Guid id, string hash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Find(id, hash) is { } existing)
        {
            return existing;
        }
        using (validation.EnterScope())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 同じ準備へ並行した照会が集まっても、大きい本文の検証を重ねません。既知の指紋照会はこの排他を取りません。
            return Find(id, hash) ?? Verify(session, id, hash, cancellationToken);
        }
    }

    /// <summary>再検査が不要な小さい確認結果だけを返します。</summary>
    /// <returns>内容版と検査期限が一致した結果です。</returns>
    private Verified? Find(Guid id, string hash)
    {
        using (gate.EnterScope())
        {
            if (entries.TryGetValue(id, out Verified? previous) && string.Equals(previous.Hash, hash, StringComparison.Ordinal)
                && clock.GetElapsedTime(previous.CheckedAt) is { TotalSeconds: >= 0 and < 30 })
            {
                return previous;
            }
        }
        return null;
    }

    /// <summary>一度に一つの本文を検査し、検証後には指紋と分類だけを残します。</summary>
    /// <returns>今回検査した小さい結果です。</returns>
    private Verified Verify(SqliteSession session, Guid id, string hash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT payload,hash FROM records WHERE kind='prepared' AND key=$id";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || !string.Equals(reader.GetString(1), hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("保存準備の内容版が一致しません。");
        }
        string payload = reader.GetString(0);
        SqlitePayload.Verify(payload, hash);
        PreparedBatch batch = JsonSerializer.Deserialize<PreparedBatch>(payload) ?? throw new InvalidDataException("保存準備がありません。");
        if (batch.OperationId != id || string.IsNullOrWhiteSpace(batch.Fingerprint) || batch.Mutations is null
            || batch.Mutations.Any(mutation => mutation is null || string.IsNullOrWhiteSpace(mutation.Scope)))
        {
            throw new InvalidDataException("保存準備の識別情報が不正です。");
        }
        Verified verified = new(hash, batch.Fingerprint, batch.Mutations.Any(mutation => DurableRepository.IsShared(mutation.Scope)), clock.GetTimestamp());
        using (gate.EnterScope())
        {
            if (!entries.ContainsKey(id) && 256 <= entries.Count)
            {
                entries.Remove(entries.Keys.First());
            }
            entries[id] = verified;
        }
        return verified;
    }

    /// <summary>本文を保持しない、最大256件の小さい確認情報です。</summary>
    private sealed record Verified(string Hash, string Fingerprint, bool Shared, long CheckedAt);
}
