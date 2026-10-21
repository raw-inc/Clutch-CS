using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>次のファイル操作がなくても終了済み予約を回収し、無関係な履歴を読まないことを検証します。</summary>
public sealed class FileReservationMaintenanceTests
{
    /// <summary>同じパスへの再実行を必要とせず、実アクセスの終了証拠だけで解放します。</summary>
    /// <returns>元の不明結果を維持し、未完了と別所有者の予約を残すことを確認します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceRecoversOnlyEndedOwnedReservationsAsync(bool localOnly)
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        socket.Close();
        FileTestPeer peer = new(Guid.NewGuid().ToString("N"), "one", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(false);
        await peer.Cluster.StartAsync();
        if (localOnly)
        {
            peer.Cluster.LocalDetachedExecutionAllowed = request => request.Kind is "file.gate.release";
            await peer.Cluster.DetachAsync();
        }
        PeerInfo member = Assert.Single(peer.Cluster.GetPeers());
        FileLease ended = Lease(member, "/ended.wav", localOnly);
        FileLease running = Lease(member, "/running.wav", localOnly);
        FileLease foreign = Lease(member, "/foreign.wav", localOnly) with { OwnerPeerId = "another" };
        string scope = localOnly ? "peer:one:file-reservations"
                                 : "file:reservations";
        NodeResult execution = NodeResult.Failure("ai.incomplete", "Original unknown outcome", EOutcome.Unknown);
        FilePhaseResult release = new(EOutcome.Unknown, "File.ReleaseUnconfirmed");
        List<DurableMutation> records = [
            new(scope, "active", null, NodeValue.FromObject(new Dictionary<Guid, FileLease> { [ended.Id] = ended, [running.Id] = running, [foreign.Id] = foreign })),
            new("peer:one:file-recovery", ended.Id.ToString("N"), null, NodeValue.FromObject(new { lease = ended, execution = execution with { FileAccessCompleted = true }, release })),
            new("peer:one:file-recovery", running.Id.ToString("N"), null, NodeValue.FromObject(new { lease = running, execution, release })),
            new("peer:one:file-recovery", foreign.Id.ToString("N"), null, NodeValue.FromObject(new { lease = foreign, execution = execution with { FileAccessCompleted = true }, release }))];
        for (int index = 0; index < 1000; index++)
        {
            // 現役予約にない旧履歴は、形式が異なっていても回収の走査対象にしません。
            records.Add(new("peer:one:file-recovery", Guid.NewGuid().ToString("N"), null, NodeValue.FromObject(new { lease = "obsolete", execution = "obsolete", release = "obsolete" })));
        }
        await peer.Repository.ApplyBatchAsync(Guid.NewGuid(), records);
        Assert.True(await peer.Transactions.RecoverReservationsAsync(localOnly));
        FileReservationLedger ledger = await FileReservationLedger.ReadAsync(peer.Repository, scope);
        Assert.True(ledger.Slotted);
        Dictionary<Guid, FileLease> retained = ledger.Active;
        Assert.Equal(2, retained.Count);
        Assert.Contains(running.Id, retained.Keys);
        Assert.Contains(foreign.Id, retained.Keys);
        JsonElement recovery = (await peer.Repository.ReadAsync("peer:one:file-recovery", ended.Id.ToString("N")))!.Value!.Json;
        Assert.Equal((int)EOutcome.Unknown, recovery.GetProperty("execution").GetProperty("Outcome").GetInt32());
        Assert.Equal((int)EOutcome.Succeeded, recovery.GetProperty("release").GetProperty("Outcome").GetInt32());
        Assert.False(await peer.Transactions.RecoverReservationsAsync(localOnly));
    }

    /// <summary>一つの現役ファイル範囲を作ります。</summary>
    /// <returns>所有実体と範囲が固定された予約です。</returns>
    private static FileLease Lease(PeerInfo member, string path, bool localOnly)
        => new(Guid.NewGuid(), member.PeerId, member.InstanceId, member, [member], [new(member.PeerId, member.InstanceId, member.HostId, path, true)], localOnly);
}
