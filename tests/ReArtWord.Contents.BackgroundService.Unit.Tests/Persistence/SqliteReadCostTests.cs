using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>ローカル診断量が共有確認の割当量とディスク書込みへ波及しないことを検証します。</summary>
public sealed class SqliteReadCostTests
{
    /// <summary>用途の絞込みは大小文字とSQL記号を含め、文字列全体の一致だけを返します。</summary>
    /// <returns>用途別索引を使う読取りの外部挙動です。</returns>
    [Fact]
    public async Task ScopedListsMatchLiteralScopeOnly()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        await repository.WriteLocalSnapshotAsync("peer:selected", "same", NodeValue.FromObject(1));
        await repository.WriteLocalSnapshotAsync("peer:Selected", "same", NodeValue.FromObject(2));
        await repository.WriteLocalSnapshotAsync("peer:' OR 1=1 --", "same", NodeValue.FromObject(3));
        Assert.Equal(1, Assert.Single(await repository.ListAsync("peer:selected")).Value!.Json.GetInt32());
        Assert.Equal(2, Assert.Single(await repository.ListAsync("peer:Selected")).Value!.Json.GetInt32());
        Assert.Equal(3, Assert.Single(await repository.ListAsync("peer:' OR 1=1 --")).Value!.Json.GetInt32());
        Assert.Empty(await repository.ListAsync("peer:missing"));
    }

    /// <summary>大きな非共有値を取得・復号せず共有版を確認します。</summary>
    /// <returns>共有版の一致と割当上限の検証です。</returns>
    [Fact]
    public async Task SharedVersionSkipsLargeLocalPayloads()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow", "a", null, NodeValue.FromObject(1))]);
        string expected = await repository.GetSharedVersionAsync();
        await repository.WriteLocalSnapshotAsync("peer:p:resources", "history", NodeValue.FromObject(new string('x', 8388608)));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(expected, await repository.GetSharedVersionAsync());
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 1048576);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("Peer:case-sensitive", "b", null, NodeValue.FromObject(2))]);
        Assert.NotEqual(expected, await repository.GetSharedVersionAsync());
    }

    /// <summary>読取りはwriter予約と併存し、初期化マーカーを再書込みしません。</summary>
    /// <returns>実SQLiteのlock下での読取りです。</returns>
    [Fact]
    public async Task ReadsDoNotTakeWriterReservationOrRewriteMarker()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        await repository.WriteLocalSnapshotAsync("peer:p", "x", NodeValue.FromObject(1));
        using FileStream marker = new(path + ".initialized", FileMode.Open, FileAccess.Read, FileShare.Read);
        using SqliteConnection writer = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await writer.OpenAsync().ConfigureAwait(true);
        using System.Data.Common.DbTransaction transaction = await writer.BeginTransactionAsync().ConfigureAwait(true);
        Assert.NotNull(await repository.ReadAsync("peer:p", "x"));
        Assert.False(await repository.HasPendingWriteAsync("peer:p", "x"));
        Assert.Single(await repository.ListAsync("peer:p"));
        Assert.Empty(await repository.GetConflictsAsync(string.Empty));
        Assert.NotEmpty(await repository.GetSharedVersionAsync());
    }

    /// <summary>初期化検証に失敗してもSQLiteのファイルハンドルを残しません。</summary>
    /// <returns>異常読取り後の実ファイル解放です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(4, Skip = "LEGACY-SPEC: 段階的履歴同期以前はversion 4を未対応としていたため。VersionFourSupportsCurrentReadAsyncで現仕様を検証します。")]
    [InlineData(5)]
    public async Task InvalidIdentityClosesTheFailedSession(int version)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync().ConfigureAwait(true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE repository_identity SET version=$version";
            command.Parameters.AddWithValue("$version", version);
            await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetSharedVersionAsync().AsTask()).ConfigureAwait(true);
        using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(0 < exclusive.Length);
    }

    /// <summary>段階的な履歴同期が使用する形式を、初期化破損と区別します。</summary>
    /// <returns>version 4でも現在値の読取りと共有版の照会が可能なことです。</returns>
    [Fact]
    public async Task VersionFourSupportsCurrentReadAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "value", null, NodeValue.FromObject(42))]);
        string expected = await repository.GetSharedVersionAsync();
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync().ConfigureAwait(true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE repository_identity SET version=4";
            await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        }
        repository = new(path);
        Assert.Equal(expected, await repository.GetSharedVersionAsync());
        Assert.Equal(42, (await repository.ReadAsync("workflow:test", "value"))!.Value!.Json.GetInt32());
    }
}
