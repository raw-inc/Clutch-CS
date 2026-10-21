using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>空き領域の回収を通常アクセスより優先せず、延期後にも整合した回収を続けます。</summary>
public sealed class HistoryReclaimContentionTests
{
    /// <summary>読取りまたは書込み中のDBを長時間塞がず、保持値を残して後から縮小します。</summary>
    /// <returns>ロック中の有限延期と、解除後の回収確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReclaimDefersDuringAccessAndResumesAfterwardsAsync(bool writer)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new("shared", "kept", null, NodeValue.FromObject(42))])).Outcome);
        CheckAndReclaim(path, writer);
        Assert.Equal(42, (await repository.ReadAsync("shared", "kept"))!.Value!.Json.GetInt32());
    }

    /// <summary>同じスレッドでロックを保持したまま回収し、解除後の空きページと整合性を確認します。</summary>
    private static void CheckAndReclaim(string path, bool writer)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE reclaim_fixture(data BLOB); INSERT INTO reclaim_fixture VALUES(zeroblob(1048576)); DROP TABLE reclaim_fixture;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA freelist_count";
        Assert.True(0 < (long)command.ExecuteScalar()!);
        long initialSize = new FileInfo(path).Length;
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: !writer))
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM records";
            Assert.True(0 < (long)command.ExecuteScalar()!);
            Stopwatch watch = Stopwatch.StartNew();
            Assert.True(SqliteHistoryRestore.Reclaim(path));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
            transaction.Commit();
        }
        command.Transaction = null;
        for (int pass = 0; pass < 16 && SqliteHistoryRestore.Reclaim(path); pass++)
        {
        }
        command.CommandText = "PRAGMA freelist_count";
        Assert.Equal(0L, command.ExecuteScalar());
        Assert.True(new FileInfo(path).Length < initialSize);
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", command.ExecuteScalar());
    }
}
