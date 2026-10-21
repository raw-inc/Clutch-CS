using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

/// <summary>バックエンド履歴へ保存する前に拒否した共有結果だけを抽出します。</summary>
internal static class SqliteRoutingResults
{
    /// <summary>無関係なローカル操作の所有情報をオブジェクト化せず、採用行の整合性を確認します。</summary>
    /// <returns>共有する確定拒否結果の所有情報です。</returns>
    internal static IReadOnlyList<RoutingOwner> Read(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = new("""
            SELECT payload,hash FROM records WHERE kind='entry' AND scope='peer:persistence-routing'
            AND json_type(payload,'$.Value.Json.Result')='object'
            AND NOT EXISTS(SELECT 1 FROM json_each(records.payload,'$.Value.Json.Operation.Scopes') AS item
                WHERE substr(item.value,1,5)='peer:' OR substr(item.value,1,9)='receipts:' OR substr(item.value,1,8)='cluster:')
            """, connection, transaction);
        using SqliteDataReader reader = command.ExecuteReader();
        List<RoutingOwner> results = [];
        while (reader.Read())
        {
            string payload = reader.GetString(0);
            SqlitePayload.Verify(payload, reader.GetString(1));
            DurableEntry? entry = JsonSerializer.Deserialize<DurableEntry>(payload);
            RoutingOwner owner = entry?.Value?.Json.Deserialize<RoutingOwner>(ContractJson.Options) ?? throw new InvalidDataException("操作所有先の保存形式が不正です。");
            if (owner.Result is not null && owner.Operation.Scopes.All(DurableRepository.IsShared))
            {
                results.Add(owner);
            }
        }
        return results;
    }
}
