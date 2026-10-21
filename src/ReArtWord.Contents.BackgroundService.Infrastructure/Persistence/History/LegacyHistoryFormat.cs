using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>互換期間だけ必要な旧本文を、通常の履歴検証と同じ有限単位で識別します。</summary>
internal static class LegacyHistoryFormat
{
    /// <summary>既存の検証済み履歴ブロックへ移す全体予約一覧の形式名です。</summary>
    internal static string ReservationList => "file-reservation-list-v1";

    /// <summary>起動では本文も既存の検証結果も変更せず、背景走査の位置だけを確保します。</summary>
    internal static void Initialize(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = new("""
            CREATE TABLE IF NOT EXISTS history_legacy_formats(
                kind TEXT NOT NULL,key TEXT NOT NULL,source_hash TEXT NOT NULL,format TEXT NOT NULL,PRIMARY KEY(kind,key));
            CREATE TRIGGER IF NOT EXISTS history_legacy_deleted AFTER DELETE ON records
            BEGIN DELETE FROM history_legacy_formats WHERE kind=OLD.kind AND key=OLD.key; END;
            CREATE TRIGGER IF NOT EXISTS history_legacy_changed AFTER UPDATE OF payload,hash ON records
            WHEN OLD.hash<>NEW.hash OR OLD.payload<>NEW.payload
            BEGIN DELETE FROM history_legacy_formats WHERE kind=OLD.kind AND key=OLD.key; END;
            CREATE TABLE IF NOT EXISTS history_legacy_scan(id INTEGER PRIMARY KEY CHECK(id=1),last_rowid INTEGER NOT NULL);
            INSERT INTO history_legacy_scan(id,last_rowid) VALUES(1,0) ON CONFLICT(id) DO NOTHING;
            """, connection, transaction);
        command.ExecuteNonQuery();
    }

    /// <summary>既に検証済みの履歴を再分類へ戻さず、小さい本文量だけから旧形式を識別します。</summary>
    internal static void Backfill(SqliteSession session, int maximum, CancellationToken cancellationToken = default)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT last_rowid FROM history_legacy_scan WHERE id=1";
        long previous = (long)command.ExecuteScalar()!;
        long last = previous;
        List<HistoryRow> rows = [];
        command.CommandText = """
            SELECT r.rowid,r.kind,r.key,r.scope,r.payload,r.hash,c.reviewed_hash FROM records r
            JOIN history_candidates c ON c.kind=r.kind AND c.key=r.key
            WHERE r.rowid>$after ORDER BY r.rowid LIMIT $maximum
            """;
        command.Parameters.AddWithValue("$after", previous);
        command.Parameters.AddWithValue("$maximum", Math.Min(16, maximum));
        long bytes = 0;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.IsDBNull(6) || !string.Equals(reader.GetString(5), reader.GetString(6), StringComparison.Ordinal))
                {
                    last = reader.GetInt64(0);
                    continue;
                }
                string payload = reader.GetString(4);
                if (rows.Count != 0 && 1048576 < bytes + payload.Length * 2L)
                {
                    break;
                }
                last = reader.GetInt64(0);
                string scope = string.Empty;
                if (!reader.IsDBNull(3))
                {
                    scope = reader.GetString(3);
                }
                rows.Add(new(reader.GetString(1), reader.GetString(2), scope, payload, reader.GetString(5)));
                bytes += payload.Length * 2L;
            }
        }
        foreach (HistoryRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SqlitePayload.Verify(row.Payload, row.Hash);
            Review(session, row);
        }
        if (last != previous)
        {
            command.CommandText = "UPDATE history_legacy_scan SET last_rowid=$last WHERE id=1";
            command.Parameters.AddWithValue("$last", last);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>旧形式を後から受信した場合も、本文の版に結び付けて移行対象へ戻します。</summary>
    internal static void Review(SqliteSession session, HistoryRow row)
    {
        string? format = Detect(row.Payload);
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "DELETE FROM history_legacy_formats WHERE kind=$kind AND key=$key";
        command.Parameters.AddWithValue("$kind", row.Kind);
        command.Parameters.AddWithValue("$key", row.Key);
        command.ExecuteNonQuery();
        if (format is null)
        {
            return;
        }
        command.CommandText = "INSERT INTO history_legacy_formats(kind,key,source_hash,format) VALUES($kind,$key,$hash,$format)";
        command.Parameters.AddWithValue("$hash", row.Hash);
        command.Parameters.AddWithValue("$format", format);
        command.ExecuteNonQuery();
    }

    /// <summary>業務値の同名プロパティを誤って移行せず、保存項目の構造で旧形式を識別します。</summary>
    /// <returns>対応済みの旧形式名、または移行対象外のnullです。</returns>
    internal static string? Detect(string payload)
    {
        // LEGACY-FORMAT: file-reservation-list-v1 — 移行済みの導入先で旧本文が発生しなくなった後に撤去します。
        string sharedScope = "file:reservations", localSuffix = ":file-reservations";
        if (!payload.Contains(sharedScope, StringComparison.Ordinal) && !payload.Contains(localSuffix, StringComparison.Ordinal))
        {
            return null;
        }
        using JsonDocument document = JsonDocument.Parse(payload);
        if (ContainsReservationList(document.RootElement))
        {
            return ReservationList;
        }
        return null;
    }

    /// <summary>receipt、複製結果、所有情報の入れ子に含まれる同じ旧台帳を識別します。</summary>
    /// <returns>予約一覧の保存項目を含むかです。</returns>
    private static bool ContainsReservationList(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            string scopeName = "Scope", keyName = "Key", activeKey = "active", valueName = "Value", jsonName = "Json", formatName = "Format";
            if (HistoryJson.TryGet(value, scopeName, out JsonElement scope) && scope.ValueKind == JsonValueKind.String
                && scope.GetString() is { } name && IsReservationScope(name)
                && HistoryJson.TryGet(value, keyName, out JsonElement key) && key.ValueEquals(activeKey)
                && HistoryJson.TryGet(value, valueName, out JsonElement node) && node.ValueKind == JsonValueKind.Object
                && HistoryJson.TryGet(node, jsonName, out JsonElement list) && list.ValueKind == JsonValueKind.Object
                && !HistoryJson.TryGet(list, formatName, out _))
            {
                return true;
            }
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (ContainsReservationList(property.Value))
                {
                    return true;
                }
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (ContainsReservationList(item))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>現在値の予約用途だけを対象にし、利用者のデータの識別子と混同しません。</summary>
    /// <returns>共有または離脱中の予約台帳かです。</returns>
    private static bool IsReservationScope(string scope)
        => scope is "file:reservations" || scope.StartsWith("peer:", StringComparison.Ordinal) && scope.EndsWith(":file-reservations", StringComparison.Ordinal);
}
