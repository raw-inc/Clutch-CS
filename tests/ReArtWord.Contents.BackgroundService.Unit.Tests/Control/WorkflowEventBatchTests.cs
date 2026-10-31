using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>事象をまとめても、発生元・重複・件数の既存境界を維持します。</summary>
public sealed class WorkflowEventBatchTests
{
    /// <summary>一括の内部事象を実UDPで受け、偽の発生元と重複事象を後続へ流しません。</summary>
    /// <returns>個別通知との互換性を含む内容確認です。</returns>
    [Fact]
    public async Task BatchedEventsKeepOriginChecksDeduplicationAndLegacyMessages()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        await using PeerCluster first = Create(cluster, "first", port, true);
        await using PeerCluster second = Create(cluster, "second", port, false);
        WorkflowEventHub firstHub = new(first.LocalPeerId, first.LocalInstanceId);
        WorkflowEventHub secondHub = new(second.LocalPeerId, second.LocalInstanceId);
        _ = new WorkflowEventExchange(first, firstHub);
        _ = new WorkflowEventExchange(second, secondHub);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await first.StartAsync(timeout.Token);
        await second.StartAsync(timeout.Token);
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, timeout.Token);
        }
        IAsyncEnumerator<WorkflowEvent> reader = secondHub.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = reader.ConfigureAwait(true);
        Task<bool> waiting = reader.MoveNextAsync().AsTask();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable waitingLifetime = new TestTaskLifetime(waiting, timeout).ConfigureAwait(true);
        WorkflowEvent begin = new(Guid.NewGuid(), "test", DateTimeOffset.UtcNow, "first", NodeValue.FromObject(1), PeerInstanceId: first.LocalInstanceId);
        WorkflowEvent end = begin with { Id = Guid.NewGuid(), Data = NodeValue.FromObject(2) };
        WorkflowEvent wrong = begin with { Id = Guid.NewGuid(), PeerId = "unrelated" };
        NodeValue batch = NodeValue.FromObject(new[] { begin, wrong, end });
        await first.PublishTransientAsync("workflow.events.batch", batch, timeout.Token);
        Assert.True(await waiting.ConfigureAwait(true));
        Assert.Equal(begin.Id, reader.Current.Id);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(end.Id, reader.Current.Id);
        Assert.True(0 < secondHub.Statistics.DroppedDeliveries);
        await first.PublishTransientAsync("workflow.events.batch", batch, timeout.Token);
        await first.PublishTransientAsync("workflow.events.batch", NodeValue.FromObject(Enumerable.Repeat(end, 33)), timeout.Token);
        WorkflowEvent legacy = begin with { Id = Guid.NewGuid(), Data = NodeValue.FromObject(3) };
        await first.PublishTransientAsync("workflow.events", NodeValue.FromObject(legacy), timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(legacy.Id, reader.Current.Id);
        Assert.Equal(3, secondHub.Statistics.Published);
    }

    /// <summary>独立した通常Peerを作ります。</summary>
    /// <returns>実発見・同期を使用する試験Peerです。</returns>
    private static PeerCluster Create(string cluster, string peer, int port, bool fresh) => new(
        new ClusterOptions(cluster, peer, peer, "127.0.0.1", "127.255.255.255", port, 1, fresh, Path.GetTempPath())
        { HeartbeatInterval = TimeSpan.FromMilliseconds(50) }, new MemoryRepository())
    { ReadinessCheck = _ => ValueTask.FromResult(true) };

    /// <summary>既存の保存規則を使う試験の状態です。</summary>
    private sealed class MemoryRepository : DurableRepository
    {
        private readonly RepositoryDocument document = new();
        private readonly Lock gate = new();

        /// <summary>一つの保存操作を排他します。</summary>
        /// <returns>その保存操作の結果です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
        {
            using (gate.EnterScope())
            {
                return operation(document);
            }
        }
    }
}
