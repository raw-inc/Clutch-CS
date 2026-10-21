using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>保存中の予約や旧版の一項目から、無関係な履歴全体を復元しないことを確認します。</summary>
public sealed class SqliteAncestryReadCostTests
{
    /// <summary>準備中の別操作があっても、小さい差分の取込みを履歴量に比例させません。</summary>
    /// <returns>予約の維持と、不要な履歴本文を作らない取込みです。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingReservationDoesNotHydrateUnrelatedHistoryAsync(bool local)
    {
        using MediaTestFiles files = new();
        SqliteRepository source = new(Path.Combine(files.Root, "source.db")), target = new(Path.Combine(files.Root, "target.db"));
        await SeedHistoryAsync(target).ConfigureAwait(true);
        string scope = "workflow:pending";
        if (local)
        {
            scope = "peer:fixture:pending";
        }
        Guid pending = Guid.NewGuid();
        IReadOnlyList<DurableMutation> preparation = await target.StampAsync([new(scope, "reserved", null, NodeValue.FromObject(1))]).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await target.PrepareAsync(pending, preparation).ConfigureAwait(true)).Outcome);
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:new", "value", null, NodeValue.FromObject(42))]).ConfigureAwait(true);
        RepositorySnapshot snapshot = await source.ExportAsync().ConfigureAwait(true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(await target.ImportAsync(snapshot).ConfigureAwait(true));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4194304);
        Assert.True(await target.HasPendingWriteAsync(scope, "reserved").ConfigureAwait(true));
        Assert.Equal(42, (await target.ReadAsync("workflow:new", "value").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal(EOutcome.Succeeded, (await target.CommitAsync(pending).ConfigureAwait(true)).Outcome);
    }

    /// <summary>同じ旧版候補の照合では祖先を探す必要がなく、別用途の過去本文を読み込みません。</summary>
    /// <returns>同じ版の保持と、履歴に依存しない小さい割当です。</returns>
    [Fact]
    public async Task MatchingLegacyEntryDoesNotHydrateUnrelatedHistoryAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository target = new(Path.Combine(files.Root, "target.db"));
        await SeedHistoryAsync(target).ConfigureAwait(true);
        DurableEntry legacy = new("workflow:legacy", "value", "legacy-version", NodeValue.FromObject(7));
        RepositorySnapshot snapshot = new([legacy], []);
        Assert.True(await target.ImportAsync(snapshot).ConfigureAwait(true));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(await target.ImportAsync(snapshot).ConfigureAwait(true));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4194304);
        Assert.Equal(legacy.Version, (await target.ReadAsync(legacy.Scope, legacy.Key).ConfigureAwait(true))!.Version);
    }

    /// <summary>操作ID版・独自GUID版・旧文字列版でも、親の内容確認と競合検出を維持します。</summary>
    /// <param name="versionKind">操作ID一致、独自GUID、旧文字列のいずれかです。</param>
    /// <param name="differentValue">同じ版の異内容を祖先として採用しないことを確認します。</param>
    /// <returns>本文を省略した差分だけからの、複数世代の祖先照合です。</returns>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task HistoricalParentsRemainVerifiedAcrossVersionFormatsAsync(int versionKind, bool differentValue)
    {
        using MediaTestFiles files = new();
        SqliteRepository target = new(Path.Combine(files.Root, "target.db"));
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        string firstVersion = firstId.ToString("N"), secondVersion = secondId.ToString("N");
        if (versionKind == 1)
        {
            firstVersion = Guid.NewGuid().ToString("N");
            secondVersion = Guid.NewGuid().ToString("N");
        }
        if (versionKind == 2)
        {
            firstVersion = "legacy-first";
            secondVersion = "legacy-second";
        }
        DurableEntry first = new("workflow:ancestry", "value", firstVersion, NodeValue.FromObject(1));
        DurableEntry second = new(first.Scope, first.Key, secondVersion, NodeValue.FromObject(2), first.Version);
        DurableEntry current = first;
        if (differentValue)
        {
            current = first with { Value = NodeValue.FromObject(99) };
        }
        Assert.True(await target.ImportAsync(new([current], [new(firstId, "first", EOutcome.Succeeded, [first]), new(secondId, "second", EOutcome.Succeeded, [second])])).ConfigureAwait(true));
        DurableEntry latest = new(first.Scope, first.Key, Guid.NewGuid().ToString("N"), NodeValue.FromObject(3), second.Version);
        Assert.Equal(!differentValue, await target.ImportAsync(new([latest], [])).ConfigureAwait(true));
        if (differentValue)
        {
            Assert.Equal(2, (await target.GetConflictsAsync(first.Scope).ConfigureAwait(true)).Count);
        }
        else
        {
            Assert.Equal(3, (await target.ReadAsync(first.Scope, first.Key).ConfigureAwait(true))!.Value!.Json.GetInt32());
        }
    }

    /// <summary>現在値を小さくし、過去本文だけが大きい実SQLiteを作ります。</summary>
    /// <returns>共有履歴と現在値を保存した完了です。</returns>
    private static async Task SeedHistoryAsync(SqliteRepository target, CancellationToken cancellationToken = default)
    {
        DurableReceipt large = await target.ApplyBatchAsync(Guid.NewGuid(), [new("file:reservations", "history", null, NodeValue.FromObject(new string('x', 8388608)))], cancellationToken).ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, (await target.ApplyBatchAsync(Guid.NewGuid(), [new("file:reservations", "history", large.Entries[0].Version, NodeValue.FromObject(0))], cancellationToken).ConfigureAwait(false)).Outcome);
    }
}
