using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>圧縮履歴の観測再利用でも、本文・索引の破損や現在値の変化を即時検出します。</summary>
public sealed class ArchivedObservationTests
{
    /// <summary>一度検証したブロックも、実バイト列や索引が変われば正常と扱いません。</summary>
    /// <returns>再起動を待たない破損検出です。</returns>
    // 抑制理由: 試験が作ったDBへInlineDataの固定破損操作だけを適用し、外部入力を受け取りません。
    [Theory]
    [InlineData("UPDATE history_blocks SET data=zeroblob(length(data))")]
    [InlineData("UPDATE history_blocks SET raw_length=raw_length+1")]
    [InlineData("UPDATE history_blocks SET hash='invalid'")]
    [InlineData("UPDATE history_index SET hash='invalid'")]
    [InlineData("UPDATE history_index SET ordinal=10000")]
    [InlineData("UPDATE history_index SET key='[\"peer:test:workflow-results\",\"wrong\"]'")]
    [InlineData("DELETE FROM history_blocks")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "固定の破損パターンを隔離した試験DBへ適用します。")]
    public async Task WarmObservationRejectsCorruptionAsync(string sql)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "archive.db");
        SqliteRepository repository = new(path);
        const string scope = "peer:test:workflow-results";
        Dictionary<string, string> known = await SeedAsync(repository, scope).ConfigureAwait(true);
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        Assert.NotEqual(0, await command.ExecuteNonQueryAsync().ConfigureAwait(true));
        Exception? error = await Record.ExceptionAsync(async () => await repository.ObserveAsync(scope, known).ConfigureAwait(false)).ConfigureAwait(true);
        Assert.True(error is InvalidDataException or EndOfStreamException, error?.ToString());
    }

    /// <summary>既知の索引を保持した後も、別の購読者へ本文を返し、削除は直ちに反映します。</summary>
    /// <returns>本文再取得、別実体、削除の整合性です。</returns>
    [Fact]
    public async Task WarmObservationCanMaterializeAndForgetDeletedEntriesAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "archive.db");
        SqliteRepository repository = new(path);
        const string scope = "peer:test:workflow-results";
        Dictionary<string, string> known = await SeedAsync(repository, scope).ConfigureAwait(true);
        DurableObservation full = Assert.Single(await repository.ObserveAsync(scope, new Dictionary<string, string>()).ConfigureAwait(true));
        Assert.Equal(10000, full.Entry!.Value!.Json.GetProperty("text").GetString()!.Length);
        Assert.Equal(known[full.Key], full.Fingerprint);
        SqliteRepository reopened = new(path);
        Assert.Null(Assert.Single(await reopened.ObserveAsync(scope, known).ConfigureAwait(true)).Entry);
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history_index";
        Assert.NotEqual(0, await command.ExecuteNonQueryAsync().ConfigureAwait(true));
        Assert.Empty(await repository.ObserveAsync(scope, known).ConfigureAwait(true));
    }

    /// <summary>実保守で一行を圧縮し、未変更観測の再利用を準備します。</summary>
    /// <returns>購読者が保持する元の指紋です。</returns>
    private static async Task<Dictionary<string, string>> SeedAsync(SqliteRepository repository, string scope, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await repository.WriteLocalSnapshotAsync(scope, "one", NodeValue.FromObject(new { CompletedAt = now.AddDays(-4), text = new string('x', 10000) }), cancellationToken).ConfigureAwait(false);
        using HistoryArchiveService archive = new(repository);
        for (int index = 0; index < 6; index++)
        {
            await archive.MaintainAsync(new(), new(1, 10), now, cancellationToken).ConfigureAwait(false);
        }
        DurableObservation first = Assert.Single(await repository.ObserveAsync(scope, new Dictionary<string, string>(), cancellationToken: cancellationToken).ConfigureAwait(false));
        Dictionary<string, string> known = new(StringComparer.Ordinal) { [first.Key] = first.Fingerprint };
        Assert.Null(Assert.Single(await repository.ObserveAsync(scope, known, cancellationToken: cancellationToken).ConfigureAwait(false)).Entry);
        return known;
    }
}
