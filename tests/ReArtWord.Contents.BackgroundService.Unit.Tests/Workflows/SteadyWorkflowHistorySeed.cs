using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>試験が新規作成したSQLiteだけに、合成履歴と基準点を準備します。</summary>
internal static class SteadyWorkflowHistorySeed
{
    /// <summary>100を指定すると約4.6GB、9.9万行、266基準点のfixtureを生成します。</summary>
    /// <returns>通常実行の計測に含めない準備の完了です。</returns>
    internal static async Task CreateAsync(SqliteRepository repository, string path)
    {
        string? configured = Environment.GetEnvironmentVariable("CONTENTS_PERFORMANCE_JOURNAL_SCALE");
        if (configured is null)
        {
            return;
        }
        int scale = int.Parse(configured, CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, 100);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await repository.WriteLocalSnapshotAsync("peer:repository-history", "policy", NodeValue.FromObject(new DataRetentionSettings(TimeZoneId: "UTC"))).ConfigureAwait(false);
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO records(kind,key,scope,payload,hash) VALUES('entry',$key,'peer:previous:workflow-results',$payload,$hash);
            INSERT INTO history_candidates(kind,key,recorded_at,eligible_after,value_scopes,hot_order,reviewed_hash)
            SELECT kind,key,$time,0,'[]',rowid,$hash FROM records WHERE kind='entry' AND key=$key;
            """;
        command.Parameters.Add("$key", SqliteType.Text);
        command.Parameters.Add("$payload", SqliteType.Text);
        command.Parameters.Add("$hash", SqliteType.Text);
        command.Parameters.AddWithValue("$time", now.ToUnixTimeSeconds());
        NodeValue value = NodeValue.FromObject(new { CompletedAt = now, detail = new string('x', 18000) });
        for (int index = 0; index < 990 * scale; index++)
        {
            string key = index.ToString("D8", CultureInfo.InvariantCulture);
            DurableEntry entry = new("peer:previous:workflow-results", key, "fixture", value);
            string payload = JsonSerializer.Serialize(entry);
            command.Parameters["$key"].Value = JsonSerializer.Serialize(new[] { entry.Scope, key });
            command.Parameters["$payload"].Value = payload;
            command.Parameters["$hash"].Value = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO history_checkpoints(id,sequence,created_at,payload,raw_length,hash,compressed,delete_at)
            VALUES($id,$sequence,$time,$payload,$length,$hash,0,$delete);
            """;
        RepositorySnapshot snapshot = new([new("benchmark:checkpoint", "value", "fixture", NodeValue.FromObject(new string('p', 10320000)))], []);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.Add("$sequence", SqliteType.Integer);
        command.Parameters.AddWithValue("$time", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$payload", data);
        command.Parameters.AddWithValue("$length", data.Length);
        command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(data)));
        command.Parameters.AddWithValue("$delete", now.AddDays(30).ToUnixTimeSeconds());
        for (int index = 0; index < (266 * scale + 99) / 100; index++)
        {
            command.Parameters["$id"].Value = "fixture-" + index.ToString(CultureInfo.InvariantCulture);
            command.Parameters["$sequence"].Value = index;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        command.Parameters.Clear();
        command.CommandText = "UPDATE history_scan SET last_rowid=(SELECT MAX(rowid) FROM records)";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }
}
