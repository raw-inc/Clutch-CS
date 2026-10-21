using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>本文を索引検索から外しても、基準点の期限変更と最新二件の保護を維持します。</summary>
public sealed class CheckpointRetentionTests
{
    /// <summary>暗黙の短縮を避け、延長・永久保持・明示短縮を同じ期限規則で扱います。</summary>
    /// <returns>実保存した基準点の圧縮後の内容と削除範囲です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexedMaintenancePreservesDeadlineAndLatestCheckpointRulesAsync(bool permanent)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int? totalDays = 30;
        if (permanent)
        {
            totalDays = null;
        }
        DataRetentionSettings initial = new(7, totalDays, CheckpointEvents: 1);
        for (int index = 0; index < 3; index++)
        {
            DurableEntry? previous = await repository.ReadAsync("workflow:test", "value").ConfigureAwait(true);
            await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "value", previous?.Version, NodeValue.FromObject(index))]).ConfigureAwait(true);
            Assert.True(SqliteHistoryCheckpoints.Capture(path, initial, now.AddDays(index - 10)));
        }
        Assert.Equal(3, SqliteHistoryCheckpoints.List(path).Length);
        SqliteHistoryCheckpoints.Maintain(path, new(1, 3), now);
        Assert.Equal(3, SqliteHistoryCheckpoints.List(path).Length);
        Assert.Single(SqliteHistoryCheckpoints.List(path), checkpoint => checkpoint.Compressed);
        VerifyCompressedContent(path);
        SqliteHistoryCheckpoints.Maintain(path, new(1, 40), now);
        SqliteHistoryCheckpoints.Maintain(path, new(1, 40), now.AddDays(25));
        Assert.Equal(3, SqliteHistoryCheckpoints.List(path).Length);
        SqliteHistoryCheckpoints.Maintain(path, new(1, 3, ApplyToExisting: true), now.AddDays(25));
        Assert.Equal(new long[] { 3, 2 }, SqliteHistoryCheckpoints.List(path).Select(checkpoint => checkpoint.Sequence));
    }

    /// <summary>圧縮済み基準点の検査値を照合し、元の業務値を復元します。</summary>
    private static void VerifyCompressedContent(string path)
    {
        using SqliteConnection connection = new("Pooling=False;Data Source=" + path);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT payload,raw_length,hash FROM history_checkpoints WHERE compressed=1 ORDER BY sequence LIMIT 1";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Infrastructure.Persistence.Documents.RepositorySnapshot snapshot = HistoryCheckpointCodec.Decode((byte[])reader.GetValue(0), reader.GetInt32(1), reader.GetString(2), true);
        Assert.Equal(0, Assert.Single(snapshot.Entries).Value!.Json.GetInt32());
    }
}
