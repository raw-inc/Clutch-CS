using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>未変更本文の省略が変更・削除・破損の見落としにならないことを検証します。</summary>
public sealed class RepositoryObservationTests
{
    /// <summary>圧縮しても同じ指紋を返し、展開した現在行への更新も検出します。</summary>
    /// <returns>通常行と圧縮履歴を跨ぐ差分観測です。</returns>
    [Fact]
    public async Task ArchivedEntryPreservesTheObservedFingerprintAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "history.db");
        SqliteRepository repository = new(path);
        string scope = "peer:test:workflow-results", key = "保存\"\\一件";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await repository.WriteLocalSnapshotAsync(scope, key, NodeValue.FromObject(new { CompletedAt = now.AddDays(-4), text = new string('x', 10000) })).ConfigureAwait(true);
        DurableObservation first = Assert.Single(await repository.ObserveAsync(scope, new Dictionary<string, string>()).ConfigureAwait(true));
        Dictionary<string, string> known = new(StringComparer.Ordinal) { [key] = first.Fingerprint };
        using HistoryArchiveService archive = new(repository);
        for (int index = 0; index < 6; index++)
        {
            await archive.MaintainAsync(new(), new(1, 10), now).ConfigureAwait(true);
        }
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM history_index WHERE kind='entry' AND scope=$scope";
        command.Parameters.AddWithValue("$scope", scope);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(true))!);
        IReadOnlySet<string> selected = new HashSet<string>(StringComparer.Ordinal) { key };
        DurableObservation unchanged = Assert.Single(await repository.ObserveAsync(scope, known, selected).ConfigureAwait(true));
        Assert.Equal(first.Fingerprint, unchanged.Fingerprint);
        Assert.Null(unchanged.Entry);
        await repository.WriteLocalSnapshotAsync(scope, key, NodeValue.FromObject(new { CompletedAt = now, text = "new" })).ConfigureAwait(true);
        DurableObservation updated = Assert.Single(await repository.ObserveAsync(scope, known, selected).ConfigureAwait(true));
        Assert.Equal("new", updated.Entry!.Value!.Json.GetProperty("text").GetString());
    }

    /// <summary>通常読取りと同じ値を取得し、変更と削除マーカーだけを差分として返します。</summary>
    /// <returns>媒体とencodingに依存しない観測結果です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ObservationsPreserveChangesDeletionAndSelectionAsync(int storage)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state");
        IDurableRepository repository = await CreateAsync(path, storage).ConfigureAwait(true);
        const string scope = "workflow.activation";
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, "a", null, NodeValue.FromObject("日本語")), new(scope, "b", null, NodeValue.FromObject(new string('x', 120000)))]).ConfigureAwait(true);
        IReadOnlyList<DurableObservation> first = await repository.ObserveAsync(scope, new Dictionary<string, string>()).ConfigureAwait(true);
        Assert.Empty(await repository.ObserveAsync(scope, new Dictionary<string, string>(), new HashSet<string>(StringComparer.Ordinal)).ConfigureAwait(true));
        Assert.Equal(2, first.Count);
        Assert.All(first, item => Assert.NotNull(item.Entry));
        Dictionary<string, string> known = first.ToDictionary(item => item.Key, item => item.Fingerprint, StringComparer.Ordinal);
        Assert.All(await repository.ObserveAsync(scope, known).ConfigureAwait(true), item => Assert.Null(item.Entry));
        DurableObservation selected = Assert.Single(await repository.ObserveAsync(scope, known, new HashSet<string>(StringComparer.Ordinal) { "a" }).ConfigureAwait(true));
        Assert.Equal("a", selected.Key);
        Assert.Empty(await repository.ObserveAsync(scope, known, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" }).ConfigureAwait(true));
        DurableEntry original = first.Single(item => string.Equals(item.Key, "a", StringComparison.Ordinal)).Entry!;
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, "a", original.Version, NodeValue.FromObject("更新"))]).ConfigureAwait(true);
        IReadOnlyList<DurableObservation> changed = await repository.ObserveAsync(scope, known).ConfigureAwait(true);
        DurableObservation updated = Assert.Single(changed, item => item.Entry is not null);
        Assert.Equal("更新", updated.Entry!.Value!.GetString());
        known[updated.Key] = updated.Fingerprint;
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, "a", updated.Entry.Version, null)]).ConfigureAwait(true);
        DurableObservation deleted = Assert.Single(await repository.ObserveAsync(scope, known).ConfigureAwait(true), item => item.Entry is not null);
        Assert.Equal("a", deleted.Key);
        Assert.Null(deleted.Entry!.Value);
        known[deleted.Key] = deleted.Fingerprint;
        Assert.All(await repository.ObserveAsync(scope, known).ConfigureAwait(true), item => Assert.Null(item.Entry));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await repository.ObserveAsync(scope, known, cancellationToken: cancellation.Token).ConfigureAwait(false)).ConfigureAwait(true);
    }

    /// <summary>キャッシュ済みでも実本文の破損を検出し、同じ版の本文変更も取り込みます。</summary>
    /// <returns>不正checksumと、正しいchecksumを持つ変更の区別です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownFingerprintStillVerifiesActualBytesAsync(bool utf16)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        int storage = utf16 ? 2
                            : 1;
        IDurableRepository repository = await CreateAsync(path, storage).ConfigureAwait(true);
        const string scope = "updates";
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, "one", null, NodeValue.FromObject("before"))]).ConfigureAwait(true);
        DurableObservation first = Assert.Single(await repository.ObserveAsync(scope, new Dictionary<string, string>()).ConfigureAwait(true));
        Dictionary<string, string> known = new(StringComparer.Ordinal) { [first.Key] = first.Fingerprint };
        DurableEntry altered = first.Entry! with { Value = NodeValue.FromObject("after") };
        string payload = JsonSerializer.Serialize(altered);
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE records SET payload=$payload WHERE kind='entry' AND scope=$scope";
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$scope", scope);
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.ObserveAsync(scope, known).ConfigureAwait(false)).ConfigureAwait(true);
        command.CommandText = "UPDATE records SET hash=$hash WHERE kind='entry' AND scope=$scope";
        command.Parameters.AddWithValue("$hash", SqlitePayload.Hash(payload));
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        DurableObservation changed = Assert.Single(await repository.ObserveAsync(scope, known).ConfigureAwait(true));
        Assert.Equal(altered.Version, changed.Entry!.Version);
        Assert.Equal("after", changed.Entry.Value!.GetString());
        command.CommandText = "DELETE FROM records WHERE kind='entry' AND scope=$scope";
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        Assert.Empty(await repository.ObserveAsync(scope, known).ConfigureAwait(true));
    }

    /// <summary>JSONと、既存UTF-16を含むSQLite媒体を同じ契約へ接続します。</summary>
    /// <returns>独立した試験用の保存先です。</returns>
    private static async Task<IDurableRepository> CreateAsync(string path, int storage, CancellationToken cancellationToken = default)
    {
        if (storage == 0)
        {
            return new JsonFileRepository(path);
        }
        if (storage == 2)
        {
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA encoding='UTF-16'; CREATE TABLE repository_identity(version INTEGER NOT NULL); INSERT INTO repository_identity VALUES(1)";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return new SqliteRepository(path);
    }
}
