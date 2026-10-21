using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>未完了の操作を持つPeerが、因果関係のある完了版だけを背景取得できることを確認します。</summary>
public sealed class StagedHistoryCompletionTests
{
    /// <summary>途中版の削除保護が、確認済み後継版の取込みを競合と誤認しません。</summary>
    /// <returns>完了版への収束、再取込みでの再発防止、および現在設定の維持です。</returns>
    [Fact]
    public async Task CompletedOperationReplacesProtectedPredecessor()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "saved", null, NodeValue.FromObject("current"))]);
        DurableEntry started = await WriteAsync(source, null, false);
        RepositorySnapshot before = await source.ExportAsync();
        Assert.True(await target.ImportAsync(before));
        DurableEntry completed = await WriteAsync(source, started, true);
        await SetBoundaryAsync(source);
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!, incoming = RecoveryRepository.Create(target)!;
        RepositorySnapshot snapshot = await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.True(await target.ImportAsync(snapshot));
        RecoveryRecord reference = Assert.Single(snapshot.DeferredHistory!, value => string.Equals(value.Entry?.Scope, "updates", StringComparison.Ordinal));
        Assert.Equal(started.Version, (await target.ReadAsync("updates", "operation"))!.Version);
        Assert.Equal(1, await incoming.FillAsync(await outgoing.ReadBodiesAsync([reference])));
        Assert.Equal(completed.Version, (await target.ReadAsync("updates", "operation"))!.Version);
        Assert.True(await target.ImportAsync(before));
        Assert.Equal(completed.Version, (await target.ReadAsync("updates", "operation"))!.Version);
        Assert.Equal("current", (await target.ReadAsync("workflows", "saved"))!.Value!.GetString());
        (_, IReadOnlyList<RecoveryRecord> remaining) = await incoming.PendingAsync();
        await incoming.FillAsync(await outgoing.ReadBodiesAsync(remaining));
        Assert.True(await target.ImportAsync(snapshot));
        Assert.Equal(0, (await incoming.StatusAsync()).Pending);
        Assert.Equal(await source.GetSharedVersionAsync(), await target.GetSharedVersionAsync());
    }

    /// <summary>別々に更新された操作を、完了フラグだけで一方へ上書きしません。</summary>
    /// <returns>独立した分岐の保持と、明示的な履歴競合です。</returns>
    [Fact]
    public async Task ConcurrentOperationCompletionRemainsConflict()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        DurableEntry started = await WriteAsync(source, null, false);
        Assert.True(await target.ImportAsync(await source.ExportAsync()));
        DurableEntry local = await WriteAsync(target, started, false);
        await WriteAsync(source, started, true);
        await SetBoundaryAsync(source);
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!, incoming = RecoveryRepository.Create(target)!;
        RepositorySnapshot snapshot = await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.True(await target.ImportAsync(snapshot));
        RecoveryRecord reference = Assert.Single(snapshot.DeferredHistory!, value => string.Equals(value.Entry?.Scope, "updates", StringComparison.Ordinal));
        Assert.Equal(0, await incoming.FillAsync(await outgoing.ReadBodiesAsync([reference])));
        Assert.Equal("Sync.HistoryConflict", (await incoming.StatusAsync()).Problem);
        Assert.Equal(local.Version, (await target.ReadAsync("updates", "operation"))!.Version);
    }

    /// <summary>背景応答を待つ間に進んだ現在版を、古い完了結果へ戻しません。</summary>
    /// <returns>新しいローカル版を保持したまま補完確認だけを終えることです。</returns>
    [Fact]
    public async Task DelayedCompletionDoesNotReplaceNewerLocalVersion()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        DurableEntry completed = await WriteAsync(source, null, true);
        Assert.True(await target.ImportAsync(await source.ExportAsync()));
        DurableEntry local = await WriteAsync(target, completed, false);
        await SetBoundaryAsync(source);
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!, incoming = RecoveryRepository.Create(target)!;
        RepositorySnapshot snapshot = await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.True(await target.ImportAsync(snapshot));
        RecoveryRecord reference = Assert.Single(snapshot.DeferredHistory!, value => string.Equals(value.Entry?.Scope, "updates", StringComparison.Ordinal));
        Assert.Equal(1, await incoming.FillAsync(await outgoing.ReadBodiesAsync([reference])));
        Assert.Equal(local.Version, (await target.ReadAsync("updates", "operation"))!.Version);
    }

    /// <summary>日時を持たない履歴でも、同期を取り直すたびに存在証明を変更しません。</summary>
    /// <returns>通信中の再同期後も同じ背景応答を適用できることです。</returns>
    [Fact]
    public async Task UnchangedHistoryReferenceSurvivesSnapshotRefresh()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        await WriteAsync(source, null, true);
        await SetBoundaryAsync(source);
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!, incoming = RecoveryRepository.Create(target)!;
        DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(2);
        RepositorySnapshot first = await outgoing.ReadAsync("source", now);
        Assert.True(await target.ImportAsync(first));
        RecoveryRecord reference = Assert.Single(first.DeferredHistory!, value => string.Equals(value.Entry?.Scope, "updates", StringComparison.Ordinal));
        IReadOnlyList<RecoveryRecordBody> bodies = await outgoing.ReadBodiesAsync([reference]);
        RepositorySnapshot refreshed = await outgoing.ReadAsync("source", now.AddMinutes(1));
        Assert.Equal(reference, Assert.Single(refreshed.DeferredHistory!, value => string.Equals(value.Entry?.Scope, "updates", StringComparison.Ordinal)));
        Assert.True(await target.ImportAsync(refreshed));
        Assert.Equal(1, await incoming.FillAsync(bodies));
    }

    /// <summary>各Peer固有の因果位置を持つ操作状態を保存します。</summary>
    /// <returns>共有保存を確認した一件です。</returns>
    private static async Task<DurableEntry> WriteAsync(IDurableRepository repository, DurableEntry? previous, bool completed)
    {
        string outcome = "unknown";
        if (completed)
        {
            outcome = "succeeded";
        }
        Infrastructure.Persistence.Receipts.DurableReceipt result = await repository.ApplyBatchAsync(Guid.NewGuid(), [new("updates", "operation", previous?.Version,
            NodeValue.FromObject(new { completed, outcome }))]).ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        return Assert.Single(result.Entries);
    }

    /// <summary>過去の完了履歴だけを背景取得の対象にします。</summary>
    /// <returns>保持境界を保存した完了です。</returns>
    private static async Task SetBoundaryAsync(IDurableRepository repository)
        => Assert.True(await repository.ImportAsync(new([], [], HistoryBoundary: new(new Dictionary<ERetentionCategory, DateTimeOffset>(),
            new Dictionary<ERetentionCategory, DateTimeOffset>(), DateTimeOffset.MinValue, DateTimeOffset.UtcNow))).ConfigureAwait(false));
}
