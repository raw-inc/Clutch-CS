using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Events;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>更新監視の本文省略が進捗や所有操作の追加を隠さないことを確認します。</summary>
public sealed class UpdateEventObservationTests
{
    /// <summary>同じ状態は通知を増やさず、完了と新しい所有操作は次の照会で通知します。</summary>
    /// <returns>状態変更と再起動を跨ぐ通知回数です。</returns>
    [Fact]
    public async Task ChangedStatusAndNewOwnershipAreObservedWithoutReplayingNotificationsAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        ReleaseManifest manifest = new("fixture", "win-x64", "app.exe", [], []);
        ClusterUpdateRequest request = new(Guid.NewGuid(), manifest, files.Root, [], EUpdateFailurePolicy.KeepCompatibleMixed);
        ClusterUpdateStatus status = new(request, EOutcome.Unknown, false, []);
        string key = request.OperationId.ToString("N"), owners = "peer:owner:update-hook-owner";
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("updates", key, null, NodeValue.FromObject(status))]).ConfigureAwait(true);
        WorkflowEventHub events = new("owner", Guid.NewGuid());
        UpdateEventPump pump = new(repository, events, "owner");
        await pump.DrainAsync().ConfigureAwait(true);
        Assert.Equal(0, events.Statistics.Published);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new(owners, key, null, NodeValue.FromObject(request.OperationId))]).ConfigureAwait(true);
        await pump.DrainAsync().ConfigureAwait(true);
        Assert.Equal(1, events.Statistics.Published);
        await pump.DrainAsync().ConfigureAwait(true);
        Assert.Equal(1, events.Statistics.Published);
        DurableEntry original = (await repository.ReadAsync("updates", key).ConfigureAwait(true))!;
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("updates", key, original.Version, NodeValue.FromObject(status with { Completed = true, Outcome = EOutcome.Succeeded }))]).ConfigureAwait(true);
        await pump.DrainAsync().ConfigureAwait(true);
        Assert.Equal(2, events.Statistics.Published);
        await pump.DrainAsync().ConfigureAwait(true);
        await new UpdateEventPump(repository, events, "owner").DrainAsync().ConfigureAwait(true);
        Assert.Equal(2, events.Statistics.Published);
        Assert.Null(pump.Problem);
        Assert.Equal(0, pump.Pending);
    }
}
