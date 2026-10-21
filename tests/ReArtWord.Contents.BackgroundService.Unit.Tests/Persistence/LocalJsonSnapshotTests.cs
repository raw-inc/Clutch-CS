using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>ローカルJSONの直接保存を既存形式、失敗境界、割当量で検証します。</summary>
public sealed class LocalJsonSnapshotTests
{
    /// <summary>UTF-16の既存DBもencodingを変えず旧形式と相互に読み書きできます。</summary>
    /// <returns>既存保存の互換性確認です。</returns>
    [Fact]
    public async Task Utf16DatabaseKeepsItsOriginalTextEncoding()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "utf16.db");
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA encoding='UTF-16'; CREATE TABLE repository_identity(version INTEGER NOT NULL); INSERT INTO repository_identity VALUES(1)";
            await command.ExecuteNonQueryAsync();
        }
        SqliteRepository repository = new(path);
        string[] original = ["旧履歴", "日本語"];
        await repository.WriteLocalSnapshotAsync("peer:p:resources", "history", NodeValue.FromObject(original));
        Assert.Equal(original, await LocalJsonSnapshots.ReadAsync<string[]>(repository, "peer:p:resources", "history"));
        string[] updated = ["更新済み", "日本語"];
        await LocalJsonSnapshots.WriteAsync(repository, "peer:p:resources", "history", writer => JsonSerializer.Serialize(writer, updated, ContractJson.Options));
        Assert.Equal(updated, (await repository.ReadAsync("peer:p:resources", "history"))!.Value!.Json.Deserialize<string[]>());
        Assert.Equal(updated, await LocalJsonSnapshots.ReadAsync<string[]>(repository, "peer:p:resources", "history"));
    }

    /// <summary>日本語・escape・大きな単一トークンを含め、既存readerとTEXT形式を維持します。</summary>
    /// <returns>旧形式との内容・ハッシュ一致です。</returns>
    [Fact]
    public async Task StreamingSnapshotPreservesExistingTextEnvelope()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "snapshot.db");
        SqliteRepository repository = new(path);
        var input = new { text = "診断\n\"" + new string('x', 100000), outcome = EOutcome.Succeeded, values = new[] { 1.5, 2.5 } };
        Assert.Equal(EOutcome.Succeeded, await LocalJsonSnapshots.WriteAsync(repository, "peer:p:resources", "history", writer => JsonSerializer.Serialize(writer, input, ContractJson.Options)));
        DurableEntry saved = (await repository.ReadAsync("peer:p:resources", "history"))!;
        Assert.Equal(JsonSerializer.Serialize(input, ContractJson.Options), saved.Value!.Json.GetRawText());
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT typeof(payload),payload,hash FROM records WHERE kind='entry'";
        using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("text", reader.GetString(0));
        Assert.Equal(JsonSerializer.Serialize(saved), reader.GetString(1));
        Assert.Equal(SqlitePayload.Hash(reader.GetString(1)), reader.GetString(2));
        AssertNoTemporaryRows(path);
        Assert.Empty((await repository.ExportAsync()).Entries);
    }

    /// <summary>二巡の間の変更や取消で、既存値が途中JSONへ置き換わりません。</summary>
    /// <returns>同じ取引のrollback確認です。</returns>
    [Fact]
    public async Task ChangedOrCancelledInputRollsBackTheWholeReplacement()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "snapshot.db");
        SqliteRepository repository = new(path);
        await repository.WriteLocalSnapshotAsync("peer:p:resources", "history", NodeValue.FromObject("original"));
        DurableEntry original = (await repository.ReadAsync("peer:p:resources", "history"))!;
        int calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await LocalJsonSnapshots.WriteAsync(repository, "peer:p:resources", "history", writer => writer.WriteNumberValue(++calls)).ConfigureAwait(false));
        Assert.Equal(original.Version, (await repository.ReadAsync("peer:p:resources", "history"))!.Version);
        using CancellationTokenSource cancellation = new();
        calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await LocalJsonSnapshots.WriteAsync(repository, "peer:p:resources", "history", writer =>
        {
            writer.WriteStringValue("replacement");
            if (++calls == 2)
            {
                cancellation.Cancel();
            }
        }, cancellation.Token).ConfigureAwait(false));
        Assert.Equal("original", (await repository.ReadAsync("peer:p:resources", "history"))!.Value!.GetString());
        AssertNoTemporaryRows(path);
        await Assert.ThrowsAsync<ArgumentException>(async () => await LocalJsonSnapshots.WriteAsync(repository, "shared:settings", "invalid", writer => writer.WriteNullValue()).ConfigureAwait(false));
    }

    /// <summary>読取り時のハッシュ検証が終わるまでは、復元した値を公開しません。</summary>
    /// <returns>破損した行の拒否です。</returns>
    [Fact]
    public async Task StreamingReadRejectsChecksumMismatch()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "corrupt.db");
        SqliteRepository repository = new(path);
        await LocalJsonSnapshots.WriteAsync(repository, "peer:p:resources", "history", writer => writer.WriteStringValue("value"));
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE records SET hash='invalid' WHERE kind='entry'";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await LocalJsonSnapshots.ReadAsync<string>(repository, "peer:p:resources", "history").ConfigureAwait(false));
    }

    /// <summary>全体サイズに比例するDOMや文字列を繰り返し割り当てません。</summary>
    /// <returns>複数MBの値でも通常片バッファーだけで保存する確認です。</returns>
    [Fact]
    public async Task RepeatedLargeSnapshotsAllocateLessThanThePayloadSize()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "snapshot.db"));
        string[] values = Enumerable.Repeat(new string('x', 1024), 4096).ToArray();
        Action<Utf8JsonWriter> serialize = writer => JsonSerializer.Serialize(writer, values, ContractJson.Options);
        await LocalJsonSnapshots.WriteAsync(repository, "peer:p:resources", "history", serialize);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 3; iteration++)
        {
            await LocalJsonSnapshots.WriteAsync(repository, "peer:p:resources", "history", serialize);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 512 * 1024);
        Assert.Equal(4096, (await repository.ReadAsync("peer:p:resources", "history"))!.Value!.Json.GetArrayLength());
    }

    /// <summary>成功・rollbackのどちらでも保存形式へ内部作業行を残しません。</summary>
    private static void AssertNoTemporaryRows(string path)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM records WHERE kind='_snap'";
        Assert.Equal(0L, command.ExecuteScalar());
    }
}
