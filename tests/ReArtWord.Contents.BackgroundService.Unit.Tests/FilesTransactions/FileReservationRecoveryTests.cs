using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>停止した旧実体の予約だけを、元結果を変えずに明示復旧します。</summary>
public sealed class FileReservationRecoveryTests
{
    /// <summary>共通境界で失敗・取消と未確認のアクセスを区別し、後続の実ファイル更新で確認します。</summary>
    /// <returns>確定終了の解放と、未確認処理の排他維持です。</returns>
    [Theory]
    [InlineData(EOutcome.Failed, false)]
    [InlineData(EOutcome.Cancelled, false)]
    [InlineData(EOutcome.NotExecuted, false)]
    [InlineData(EOutcome.Unknown, true)]
    [InlineData(EOutcome.Unknown, false)]
    public async Task CommonBoundaryReleasesEndedWorkWithoutReplayingItAsync(EOutcome outcome, bool ended)
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        socket.Close();
        ControlledFileNode? controlled = null;
        FileTestPeer peer = await FileTestPeer.CreateAsync(Guid.NewGuid().ToString("N"), "one", port, true, node =>
        {
            const string upsert = "file.upsert";
            if (!string.Equals(node.Descriptor.Type, upsert, StringComparison.Ordinal))
            {
                return node;
            }
            controlled = new((ICompensableFileNode)node)
            {
                BeforeExecution = _ => Task.FromResult<NodeResult?>(NodeResult.Failure("fixture.ended", "Fixture outcome", outcome) with { FileAccessCompleted = ended }),
            };
            return controlled;
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.Cluster.StartAsync().ConfigureAwait(true);
        string path = Path.Combine(peer.Root, "output.txt");
        await File.WriteAllTextAsync(path, "before").ConfigureAwait(true);
        NodeExecutionContext context = new(Guid.NewGuid(), Guid.NewGuid(), "fixture", "revision", "write", "one", JsonSerializer.SerializeToElement(new { path }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("after") }, TimeProvider.System);
        Assert.Equal(outcome, (await peer.Transactions.ExecuteFileAsync(controlled!, context).ConfigureAwait(true)).Outcome);
        Assert.Equal(1, controlled!.Executions);
        controlled.BeforeExecution = null;
        NodeResult next = await peer.Transactions.ExecuteFileAsync(controlled, context with { ExecutionId = Guid.NewGuid() }).ConfigureAwait(true);
        bool released = outcome != EOutcome.Unknown || ended;
        Assert.Equal(released ? EOutcome.Succeeded
                              : EOutcome.NotExecuted, next.Outcome);
        Assert.Equal(released ? "after"
                              : "before", await File.ReadAllTextAsync(path).ConfigureAwait(true));
        Assert.Equal(released ? 2
                              : 1, controlled.Executions);
    }

    /// <summary>明示確認・所有者の照合・旧実体という条件を満たした一件だけを解放します。</summary>
    /// <returns>誤解放の拒否、元の不明結果の保存、再要求の冪等性です。</returns>
    [Fact]
    public async Task ExplicitReleasePreservesTheOriginalUnknownResultAsync()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        socket.Close();
        FileTestPeer peer = await FileTestPeer.CreateAsync(Guid.NewGuid().ToString("N"), "one", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.Cluster.StartAsync().ConfigureAwait(true);
        ReArtWord.Contents.BackgroundService.Contracts.Cluster.PeerInfo member = Assert.Single(peer.Cluster.GetPeers());
        Guid originalInstance = Guid.NewGuid(), id = Guid.NewGuid();
        FileLease lease = new(id, "one", originalInstance, member, [member], [new("one", originalInstance, "one", "/output.wav", true)]);
        NodeResult execution = NodeResult.Failure("ai.incomplete", "The original provider result is unknown.", EOutcome.Unknown);
        FilePhaseResult release = new(EOutcome.NotExecuted, "File.ReservationRetained");
        string scope = "peer:one:file-recovery", key = id.ToString("N");
        await peer.Repository.ApplyBatchAsync(Guid.CreateVersion7(), [
            new("file:reservations", "active", null, NodeValue.FromObject(new Dictionary<Guid, FileLease> { [id] = lease })),
            new(scope, key, null, NodeValue.FromObject(new { lease, execution, release }))]).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, (await peer.Transactions.ReleaseReservationAsync(new(id, originalInstance, false)).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await peer.Transactions.ReleaseReservationAsync(new(id, member.InstanceId, true)).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await peer.Transactions.ReleaseReservationAsync(new(id, Guid.NewGuid(), true)).ConfigureAwait(true)).Outcome);
        Assert.Single((await FileReservationLedger.ReadAsync(peer.Repository, "file:reservations").ConfigureAwait(true)).Active);
        FilePhaseResult completed = await peer.Transactions.ReleaseReservationAsync(new(id, originalInstance, true)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, completed.Outcome);
        Assert.Empty((await FileReservationLedger.ReadAsync(peer.Repository, "file:reservations").ConfigureAwait(true)).Active);
        JsonElement stored = (await peer.Repository.ReadAsync(scope, key).ConfigureAwait(true))!.Value!.Json;
        Assert.Equal((int)EOutcome.Unknown, stored.GetProperty("execution").GetProperty("Outcome").GetInt32());
        Assert.Equal("ai.incomplete", stored.GetProperty("execution").GetProperty("ReasonCode").GetString());
        Assert.True(stored.GetProperty("fileAccessEndedConfirmed").GetBoolean());
        Assert.Equal(completed, await peer.Transactions.ReleaseReservationAsync(new(id, originalInstance, true)).ConfigureAwait(true));
    }
}
