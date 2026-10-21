using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>通常の受付で過去本文を走査せず、固定サイズの境界と一つの索引だけを確認します。</summary>
internal static class SqliteHistoryGuard
{
    /// <summary>削除索引と本文の不存在を同じ読取り取引で確認します。</summary>
    /// <returns>再利用できない受付キーかです。</returns>
    internal static bool IsRetiredKey(string path, string scope, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        return IsRetiredKey(session, scope, key, requireAbsentForTime: true);
    }

    /// <summary>確認済み本文が消えた操作を未実行へ戻しません。</summary>
    /// <returns>削除後の確認不能、未処理ならnullです。</returns>
    internal static DurableReceipt? Read(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using SqliteCommand command = new("SELECT fingerprint,shared FROM retired_operations WHERE id=$id", connection, transaction);
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.CommandText = "SELECT 1 FROM records WHERE kind='prepared' AND key=$id";
        if (command.ExecuteScalar() is not null)
        {
            return null;
        }
        command.CommandText = "SELECT fingerprint,shared FROM retired_operations WHERE id=$id";
        RetiredOperation? retired = null;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                retired = new(id, reader.GetString(0), reader.GetInt32(1) != 0);
            }
        }
        command.CommandText = "SELECT payload,hash FROM records WHERE kind='entry' AND key=$key";
        command.Parameters.AddWithValue("$key", DurableRepository.GetAddress(RepositoryHistoryGuard.Scope, "boundary"));
        using SqliteDataReader boundaryReader = command.ExecuteReader();
        HistoryRetentionBoundary? boundary = null;
        if (boundaryReader.Read())
        {
            string payload = boundaryReader.GetString(0);
            SqlitePayload.Verify(payload, boundaryReader.GetString(1));
            boundary = JsonSerializer.Deserialize<DurableEntry>(payload)?.Value?.Json.Deserialize<HistoryRetentionBoundary>();
        }
        return RepositoryHistoryGuard.Expired(id, boundary, retired);
    }

    /// <summary>新規要求のCASでだけ、消えた受付キーを再利用できないことを確認します。</summary>
    /// <returns>明示的に削除したキーかです。</returns>
    internal static bool IsRetiredKey(SqliteSession session, string scope, string key, bool requireAbsentForTime = false)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT 1 FROM retired_keys WHERE scope=$scope AND key=$key";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$key", key);
        if (command.ExecuteScalar() is not null)
        {
            return true;
        }
        if (!RepositoryHistoryGuard.IsSingleUse(scope) || !RepositoryHistoryGuard.TryOperationId(key, out Guid id))
        {
            return false;
        }
        string entryKind = "entry";
        if (requireAbsentForTime && session.Read<DurableEntry>(entryKind, DurableRepository.GetAddress(scope, key)) is not null)
        {
            return false;
        }
        DurableEntry? retained = session.Read<DurableEntry>("entry", DurableRepository.GetAddress(RepositoryHistoryGuard.Scope, "boundary"));
        return RepositoryHistoryGuard.Expired(id, retained?.Value?.Json.Deserialize<HistoryRetentionBoundary>()) is not null;
    }

    /// <summary>一つの同期取引に境界を読み込みます。</summary>
    internal static void Load(SqliteSession session, RepositoryDocument document)
    {
        string address = DurableRepository.GetAddress(RepositoryHistoryGuard.Scope, "boundary");
        DurableEntry? entry = session.Read<DurableEntry>("entry", address);
        if (entry is not null)
        {
            document.Entries[address] = entry;
        }
    }

    /// <summary>共有操作の小さい索引だけを同期対象にします。</summary>
    /// <returns>ローカル要求を含まない旧操作の識別情報です。</returns>
    internal static RetiredOperation[] Export(SqliteSession session)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT id,fingerprint,outcome,reason FROM retired_operations WHERE shared=1";
        List<RetiredOperation> result = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? reason = null;
            if (!reader.IsDBNull(3))
            {
                reason = reader.GetString(3);
            }
            result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), true, (Contracts.Execution.EOutcome)reader.GetInt32(2), reason));
        }
        return result.ToArray();
    }

    /// <summary>削除済み識別子を新規操作へ戻さないよう、同じ取引に保存します。</summary>
    internal static void Save(SqliteSession session, RepositoryDocument document)
    {
        using SqliteCommand command = session.HistoryCommand();
        if (document.History is not null)
        {
            command.CommandText = "UPDATE repository_identity SET version=MAX(version,3) WHERE version<3";
            command.ExecuteNonQuery();
        }
        foreach (RetiredOperation operation in document.History?.RetiredOperations.Values.AsEnumerable() ?? [])
        {
            command.CommandText = "INSERT INTO retired_operations(id,fingerprint,shared,outcome,reason) VALUES($id,$fingerprint,$shared,$outcome,$reason) ON CONFLICT(id) DO NOTHING";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", operation.Id.ToString("N"));
            command.Parameters.AddWithValue("$fingerprint", operation.Fingerprint);
            command.Parameters.AddWithValue("$shared", operation.Shared);
            command.Parameters.AddWithValue("$outcome", (int)operation.Outcome);
            command.Parameters.AddWithValue("$reason", (object?)operation.Reason ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        foreach (string address in document.History?.RetiredKeys.AsEnumerable() ?? [])
        {
            string[] parts = JsonSerializer.Deserialize<string[]>(address)!;
            command.CommandText = "INSERT INTO retired_keys(scope,key) VALUES($scope,$key) ON CONFLICT(scope,key) DO NOTHING";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$scope", parts[0]);
            command.Parameters.AddWithValue("$key", parts[1]);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>共有された履歴の受付だけを同期します。</summary>
    /// <returns>本文を再作成できない共有キーです。</returns>
    internal static RetiredHistoryKey[] ExportKeys(SqliteSession session)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT scope,key FROM retired_keys WHERE " + SqliteSharedFingerprint.ScopeFilter;
        List<RetiredHistoryKey> keys = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            keys.Add(new(reader.GetString(0), reader.GetString(1)));
        }
        return keys.ToArray();
    }
}
