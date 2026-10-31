using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.CoreService.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>実通信とSQLiteを使用し、所有元・版互換・照会の非永続性を確認します。</summary>
[Collection("TimingSensitive")]
public sealed class SourceScopeBindingTests
{
    /// <summary>未対応先への配送を拒否し、対応後の継続確認では保存台帳を増やしません。</summary>
    /// <returns>実体を固定した所有登録、終了確認と読み取り専用照会です。</returns>
    [Fact]
    public async Task RemoteCapabilityAndOwnershipChecksDoNotAccumulateReceiptsAsync()
    {
        using MediaTestFiles files = new();
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string cluster = Guid.NewGuid().ToString("N");
        string firstPath = Path.Combine(files.Root, "a.db"), secondPath = Path.Combine(files.Root, "b.db");
        PeerCluster first = Peer(cluster, "a", port, true, firstPath);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        PeerCluster second = Peer(cluster, "b", port, false, secondPath);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2 || second.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(25, timeout.Token).ConfigureAwait(true);
        }
        WorkflowFixture firstRuntime = new(), secondRuntime = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstRuntimeLifetime = firstRuntime.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondRuntimeLifetime = secondRuntime.ConfigureAwait(true);
        SourceScopeBinding owner = new(first), receiver = new(second);
        owner.Register(firstRuntime.Sources);
        SourceScope scope = owner.Create("w");
        RemoteExecutionRequest request = new(Guid.NewGuid(), Guid.NewGuid(), "w", "revision", WorkflowFixture.Definition("source"),
            new Dictionary<string, NodeValue>(), "b", second.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1)) { SourceScopes = [scope] };
        Assert.Equal("source.scope.unsupported", (await owner.CheckTargetAsync(request, timeout.Token).ConfigureAwait(true))?.ReasonCode);
        receiver.Register(secondRuntime.Sources);
        Assert.Null(await owner.CheckTargetAsync(request, timeout.Token).ConfigureAwait(true));
        Assert.True(await receiver.CheckAsync(scope, true, timeout.Token).ConfigureAwait(true));
        long initial = Receipts(firstPath) + Receipts(secondPath);
        for (int index = 0; index < 32; index++)
        {
            Assert.True(await receiver.CheckAsync(scope, false, timeout.Token).ConfigureAwait(true));
            Assert.Null(await owner.CheckTargetAsync(request, timeout.Token).ConfigureAwait(true));
        }
        Assert.Equal(initial, Receipts(firstPath) + Receipts(secondPath));
        Assert.False(await receiver.CheckAsync(scope with { PeerInstanceId = Guid.NewGuid() }, false, timeout.Token).ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await owner.EndAsync(scope, timeout.Token).ConfigureAwait(true)).Outcome);
        Assert.False(await receiver.CheckAsync(scope, false, timeout.Token).ConfigureAwait(true));
        Assert.False(await receiver.CheckAsync(scope, true, timeout.Token).ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await owner.EndAsync(scope, timeout.Token).ConfigureAwait(true)).Outcome);
    }

    /// <summary>テスト固有のポート・保存先だけを使用します。</summary>
    /// <returns>永続化と実UDP/TCPを備える小さなPeerです。</returns>
    private static PeerCluster Peer(string cluster, string id, int port, bool initialize, string path)
        => new(new ClusterOptions(cluster, id, id, "127.0.0.1", "127.255.255.255", port, 1, initialize, Path.GetDirectoryName(path)!)
        { HeartbeatInterval = TimeSpan.FromMilliseconds(100), PeerTimeout = TimeSpan.FromSeconds(2), RequestTimeout = TimeSpan.FromSeconds(2) }, new SqliteRepository(path))
        { ReadinessCheck = _ => ValueTask.FromResult(true) };

    /// <summary>通信照会が操作記録へ誤登録されていないことを、実保存先で数えます。</summary>
    /// <returns>保存済み操作記録数です。</returns>
    private static long Receipts(string path)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM records WHERE kind='receipt'";
        return (long)command.ExecuteScalar()!;
    }
}
