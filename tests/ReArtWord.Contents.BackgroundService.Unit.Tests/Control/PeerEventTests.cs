using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>相手から通知できない切断を観測元から通知し、ハートビートで水増ししないことを確認します。</summary>
public sealed class PeerEventTests
{
    /// <summary>参加・担当・通信不能・観測終了を、元のPeer実体を区別して購読できます。</summary>
    /// <returns>意味のある状態変化だけの通知確認。</returns>
    [Fact]
    public async Task ObservedPeerChangesIgnoreHeartbeatUpdatesAsync()
    {
        WorkflowFixture.TestPeers peers = new();
        PeerInfo remote = new("b", Guid.NewGuid(), "host-b", "127.0.0.1", 20000, EPeerStatus.Ready, 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        peers.Others = [remote];
        WorkflowEventHub events = new(peers.LocalPeerId, peers.LocalInstanceId);
        PeerEventObservations observations = new(peers, events);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        IAsyncEnumerator<WorkflowEvent> reader = events.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable readerLifetime = reader.ConfigureAwait(true);
        Task<bool> waiting = reader.MoveNextAsync().AsTask();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable waitingLifetime = new TestTaskLifetime(waiting, timeout).ConfigureAwait(true);
        await observations.RefreshAsync(timeout.Token).ConfigureAwait(true);
        Assert.True(await waiting.ConfigureAwait(true));
        Assert.Equal("peer.changed", reader.Current.Name);
        Assert.Equal("a", reader.Current.PeerId);
        Assert.Equal("b", reader.Current.Data.Json.GetProperty("current").GetProperty("peerId").GetString());
        Assert.Equal(remote.InstanceId, reader.Current.Data.Json.GetProperty("current").GetProperty("instanceId").GetGuid());
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal("cluster.coordinatorChanged", reader.Current.Name);
        peers.Others = [remote with { LastSeen = remote.LastSeen.AddSeconds(10), ReadySince = remote.ReadySince?.AddSeconds(10) }];
        await observations.RefreshAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(2, events.Statistics.Published);
        peers.Others = [remote with { Status = EPeerStatus.Unavailable }];
        await observations.RefreshAsync(timeout.Token).ConfigureAwait(true);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal("ready", reader.Current.Data.Json.GetProperty("previous").GetProperty("status").GetString());
        Assert.Equal("unavailable", reader.Current.Data.Json.GetProperty("current").GetProperty("status").GetString());
        peers.Others = [];
        await observations.RefreshAsync(timeout.Token).ConfigureAwait(true);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, reader.Current.Data.Json.GetProperty("current").ValueKind);
        Assert.Equal(4, events.Statistics.Published);
    }
}
