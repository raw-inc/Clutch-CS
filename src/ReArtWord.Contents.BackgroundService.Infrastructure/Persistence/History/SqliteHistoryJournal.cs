using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>現在状態の確定と同じ取引で、共有イベントの順序と親版情報を記録します。</summary>
internal static class SqliteHistoryJournal
{
    /// <summary>同じ操作の再送やprepareだけでは確定件数を進めません。</summary>
    internal static void Record(SqliteConnection connection, SqliteTransaction transaction, DurableReceipt receipt)
    {
        if (receipt.Outcome != EOutcome.Succeeded || receipt.Entries.Count == 0 || receipt.Entries.Any(entry => !DurableRepository.IsShared(entry.Scope)))
        {
            return;
        }
        RepositoryDocument metadata = new();
        using SqliteCommand query = new("SELECT payload,hash FROM records WHERE kind='entry' AND key=$key", connection, transaction);
        foreach (DurableEntry entry in receipt.Entries)
        {
            string address = DurableRepository.GetAddress(RepositoryCausality.Scope, RepositoryCausality.EntryKey(entry.Scope, entry.Key));
            query.Parameters.Clear();
            query.Parameters.AddWithValue("$key", address);
            using SqliteDataReader reader = query.ExecuteReader();
            if (reader.Read())
            {
                string payload = reader.GetString(0);
                SqlitePayload.Verify(payload, reader.GetString(1));
                metadata.Entries[address] = JsonSerializer.Deserialize<DurableEntry>(payload)!;
            }
        }
        RepositoryCausalEntry[] causal = RepositoryCausality.Export(metadata, receipt.Entries);
        if (causal.Length != receipt.Entries.Count)
        {
            return;
        }
        query.CommandText = "INSERT INTO history_events(operation,recorded_at,causal) SELECT $operation,$recorded,$causal WHERE NOT EXISTS(SELECT 1 FROM history_events WHERE operation=$operation)";
        query.Parameters.Clear();
        query.Parameters.AddWithValue("$operation", receipt.OperationId.ToString("N"));
        query.Parameters.AddWithValue("$recorded", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        query.Parameters.AddWithValue("$causal", JsonSerializer.Serialize(causal));
        query.ExecuteNonQuery();
    }
}
