using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>元要求の照会結果を再送せず取り込み、確認済み値を未保存値と分けます。</summary>
public sealed class AvailabilityResolutionTests
{
    /// <summary>成功確認は元IDだけへ適用し、未保存43を確認済み42の代用にしません。</summary>
    /// <returns>照会確定と本文の分離です。</returns>
    [Fact]
    public async Task SuccessfulReceiptResolvesOriginalWithoutResending()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        using AvailabilityService service = Create(repository, files.Root);
        await service.RememberConfirmedAsync(EStoragePurpose.Runtime, "data", NodeValue.FromObject(42), "r1", true).ConfigureAwait(true);
        await service.StageOfflineAsync(EStoragePurpose.Runtime, "data", NodeValue.FromObject(43), "r1").ConfigureAwait(true);
        AvailabilityValue confirmed = service.TryGetConfirmed(EStoragePurpose.Runtime, "data")!;
        Assert.Equal(42, confirmed.Value!.Json.GetInt32());
        Assert.Equal("r1", confirmed.Revision);
        Assert.False(confirmed.State.IsUnsaved);
        Assert.False(confirmed.State.IsLocalUnconfirmed);
        Assert.False(confirmed.State.CanWrite);
        Guid original = Guid.Empty;
        int writes = 0;
        Task<AvailabilityWriteResult> Write(string? expected, NodeValue value, Guid operation, CancellationToken token)
        {
            writes++;
            original = operation;
            return Task.FromResult(new AvailabilityWriteResult(EOutcome.Unknown, false, null));
        }
        await service.ReconcileAsync(EStoragePurpose.Runtime, "data", Write).ConfigureAwait(true);
        AvailabilityWriteResult receipt = new(EOutcome.Succeeded, true, new(NodeValue.FromObject(43), "r2", confirmed.State));
        Assert.False((await service.ResolveReconciliationAsync(EStoragePurpose.Runtime, "data", Guid.NewGuid(), receipt).ConfigureAwait(true)).Applied);
        AvailabilityWriteResult applied = await service.ResolveReconciliationAsync(EStoragePurpose.Runtime, "data", original, receipt).ConfigureAwait(true);
        Assert.True(applied.Applied);
        Assert.False(applied.Current!.State.IsUnsaved);
        Assert.Equal("r2", service.TryGetConfirmed(EStoragePurpose.Runtime, "data")!.Revision);
        await service.ReconcileAsync(EStoragePurpose.Runtime, "data", Write).ConfigureAwait(true);
        await service.ResolveReconciliationAsync(EStoragePurpose.Runtime, "data", original, receipt).ConfigureAwait(true);
        Assert.Equal(1, writes);
    }

    /// <summary>確定拒否は候補を残して新規編集を許し、元要求を再送しません。</summary>
    /// <returns>未知解除と新しい操作IDの検証です。</returns>
    [Fact]
    public async Task RejectedReceiptAllowsNewDraftButNotOriginalRetry()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        using AvailabilityService service = Create(repository, files.Root);
        await service.RememberConfirmedAsync(EStoragePurpose.Runtime, "data", NodeValue.FromObject(1), "r1", true).ConfigureAwait(true);
        AvailabilityWriteResult staged = await service.StageOfflineAsync(EStoragePurpose.Runtime, "data", NodeValue.FromObject(2), "r1").ConfigureAwait(true);
        Guid original = Guid.Empty;
        int writes = 0;
        Task<AvailabilityWriteResult> Write(string? expected, NodeValue value, Guid operation, CancellationToken token)
        {
            writes++;
            original = operation;
            return Task.FromResult(new AvailabilityWriteResult(EOutcome.Unknown, false, null));
        }
        await service.ReconcileAsync(EStoragePurpose.Runtime, "data", Write).ConfigureAwait(true);
        Guid rejectedId = original;
        await service.ResolveReconciliationAsync(EStoragePurpose.Runtime, "data", original, new(EOutcome.Unknown, false, null)).ConfigureAwait(true);
        Assert.False(service.TryGetCurrent(EStoragePurpose.Runtime, "data")!.State.CanWrite);
        AvailabilityWriteResult rejected = await service.ResolveReconciliationAsync(EStoragePurpose.Runtime, "data", original, new(EOutcome.NotExecuted, false, null)).ConfigureAwait(true);
        Assert.True(rejected.Current!.State.CanWrite);
        Assert.Equal(2, rejected.Current.Value!.Json.GetInt32());
        await service.ReconcileAsync(EStoragePurpose.Runtime, "data", Write).ConfigureAwait(true);
        Assert.Equal(1, writes);
        Assert.True((await service.StageOfflineAsync(EStoragePurpose.Runtime, "data", NodeValue.FromObject(3), staged.Current!.Revision).ConfigureAwait(true)).Applied);
        await service.ReconcileAsync(EStoragePurpose.Runtime, "data", Write).ConfigureAwait(true);
        Assert.Equal(2, writes);
        Assert.NotEqual(rejectedId, original);
    }

    /// <summary>再接続可能な内部値だけの明示設定を作ります。</summary>
    /// <returns>実リポジトリを元にする候補管理です。</returns>
    private static AvailabilityService Create(IDurableRepository repository, string root) => new("a", new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Runtime] = new(Offline: EAvailabilityOffline.LocalUnconfirmed) }), repository, root);
}
