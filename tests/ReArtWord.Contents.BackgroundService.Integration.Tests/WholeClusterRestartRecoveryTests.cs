using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>全実Core停止後の保存準備・担当競合・旧結果不一致を、実通信で同時に復旧します。</summary>
[Collection("Performance")]
public sealed class WholeClusterRestartRecoveryTests(ITestOutputHelper output)
{
    private static readonly string[] selectionHosts = ["host-a", "host-b"];
    private static readonly string[] peerIds = ["a", "b", "c"];

    /// <summary>閉じた試験DBへ停止位置を再現し、現在値の収束とOSCの後続処理まで確認します。</summary>
    /// <returns>三つの独立プロセスのReady、履歴補完、予約解放と実受信です。</returns>
    [Fact]
    public async Task AllPeersRecoverPreparedAndConflictedStateWithoutWorkflowReplayAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int clusterPort = CorePeer.AvailablePort(), inputPort = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", clusterPort, true, "host-a", 1000);
        CorePeer second = new(cluster, "b", clusterPort, false, "host-b", 900);
        CorePeer third = new(cluster, "c", clusterPort, false, "host-c", 800);
        await using ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await using ConfiguredAsyncDisposable thirdLifetime = third.ConfigureAwait(true);
        CorePeer[] peers = [first, second, third];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(150));
        foreach (CorePeer peer in peers)
        {
            await peer.StartAsync(timeout.Token);
        }
        await WaitReadyAsync(peers, timeout.Token);
        WorkflowDefinition workflow = new("restart-source", "Restart source", [
            new("pc", "execution.pc.select", JsonSerializer.SerializeToElement(new { mode = "selected", hostIds = selectionHosts })),
            new("receive", "osc.udp.receive", JsonSerializer.SerializeToElement(new { interfaceId = first.InterfaceId, localAddress = "127.0.0.1", localPort = inputPort }), StartOnLoad: true),
            new("preview", "control.preview", JsonSerializer.SerializeToElement(new { mediaType = "text/plain" }), Target: new(EExecutionScope.One, SourcePeer: true)),
        ], [new("pc", "value", "receive", "$execution.pc"), new("receive", "value", "preview", "value")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), workflow, null), timeout.Token);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => Listening(await first.GetAsync<JsonElement>("status", token).ConfigureAwait(false)), timeout.Token);
        PeerInfo[] members = (await first.GetAsync<JsonElement>("status", timeout.Token)).GetProperty("peers").Deserialize<PeerInfo[]>(ContractJson.Options)!;
        await Task.WhenAll(peers.Select(peer => peer.StopAsync(timeout.Token)));
        List<IDurableRepository> stores = [];
        foreach ((CorePeer peer, string id) in peers.Zip(peerIds))
        {
            stores.Add(await RepositoryFactory.CreateAsync(Path.Combine(peer.DirectoryPath, "state"), id, new(), timeout.Token));
        }
        await SeedAssignmentsAsync(stores, members, timeout.Token);
        IReadOnlyList<(Guid Id, EOutcome Expected)> operations = await SeedPreparationsAsync(stores, members, timeout.Token);
        Guid legacy = await SeedLegacyResultAsync(stores, timeout.Token);
        foreach (IDurableRepository store in stores)
        {
            DurableEntry? previous = await store.ReadAsync("file:reservations", "active", timeout.Token);
            Assert.Equal(EOutcome.Succeeded, (await store.ApplyBatchAsync(Guid.NewGuid(), [new("file:reservations", "active", previous?.Version,
                NodeValue.FromObject(new Dictionary<string, string>()))], timeout.Token)).Outcome);
        }
        foreach (CorePeer peer in peers)
        {
            await peer.StartAsync(timeout.Token);
        }
        await WaitReadyAsync(peers, timeout.Token);
        await CorePeer.WaitAsync(async token =>
        {
            foreach (IDurableRepository store in stores)
            {
                foreach ((Guid id, EOutcome expected) in operations)
                {
                    if (await store.ReadPreparationAsync(id, token).ConfigureAwait(false) is not null
                        || (await store.ReadReceiptAsync(id, token).ConfigureAwait(false))?.Outcome != expected)
                    {
                        return false;
                    }
                }
                if ((await store.ReadReceiptAsync(legacy, token).ConfigureAwait(false))?.Outcome != EOutcome.Succeeded)
                {
                    return false;
                }
            }
            return Listening(await first.GetAsync<JsonElement>("status", token).ConfigureAwait(false));
        }, TimeSpan.FromSeconds(60), timeout.Token);
        foreach (CorePeer peer in peers)
        {
            Assert.Empty((await peer.GetAsync<ConflictListPage>("conflicts.list", timeout.Token)).Entries);
        }
        using (UdpClient sender = new(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            await sender.SendAsync(Encoding.UTF8.GetBytes("/probe\0\0,s\0\0payload\0"), new IPEndPoint(IPAddress.Loopback, inputPort), timeout.Token);
        }
        await CorePeer.WaitAsync(async token => (await first.GetAsync<NodePreviewSnapshot[]>("previews.list", new NodePreviewQuery(workflow.Id, ["preview"]), token).ConfigureAwait(false))
            .Any(snapshot => snapshot.Problem is null && string.Equals(snapshot.Revision, saved.Revision, StringComparison.Ordinal)), timeout.Token);
        output.WriteLine(first.DirectoryPath);
        output.WriteLine("Three Core processes recovered; all prepared operations finished; legacy receipt and assignments converged; one loopback OSC signal reached Preview.");
    }

    /// <summary>現在の選択設定は変えず、旧実体を指す独立した担当版を各DBへ残します。</summary>
    /// <returns>本番と同種の内部担当分岐です。</returns>
    private static async Task SeedAssignmentsAsync(List<IDurableRepository> stores, PeerInfo[] members, CancellationToken cancellationToken = default)
    {
        string scope = SourcePcAssignments.ScopePrefix + "restart-source";
        for (int index = 0; index < stores.Count; index++)
        {
            DurableEntry old = Assert.IsType<DurableEntry>(await stores[index].ReadAsync(scope, "receive", cancellationToken).ConfigureAwait(false));
            SourcePcAssignment assignment = old.Value!.Json.Deserialize<SourcePcAssignment>(ContractJson.Options)!;
            string target = "a";
            if (index == 1)
            {
                target = "b";
            }
            SourcePcAssignment branch = assignment with { OperationId = Guid.NewGuid(), Targets = [members.Single(peer => string.Equals(peer.PeerId, target, StringComparison.Ordinal))] };
            Assert.Equal(EOutcome.Succeeded, (await stores[index].ApplyBatchAsync(branch.OperationId, [new(scope, "receive", old.Version, NodeValue.FromObject(branch))], cancellationToken).ConfigureAwait(false)).Outcome);
        }
    }

    /// <summary>準備途中と決定後の停止位置を、同じ保存APIと操作IDで作ります。</summary>
    /// <returns>再起動後に確認する元結果です。</returns>
    private static async Task<IReadOnlyList<(Guid Id, EOutcome Expected)>> SeedPreparationsAsync(List<IDurableRepository> stores, PeerInfo[] members, CancellationToken cancellationToken = default)
    {
        List<(Guid, EOutcome)> results = [];
        for (int index = 0; index < 3; index++)
        {
            Guid id = Guid.NewGuid();
            IReadOnlyList<DurableMutation> mutations = await stores[0].StampAsync([new("workflow:reboot-proof", "prepared-" + index, null, NodeValue.FromObject(index))], cancellationToken).ConfigureAwait(false);
            DurableReceipt? receipt = null;
            foreach (IDurableRepository store in stores)
            {
                receipt = await store.PrepareAsync(id, mutations, cancellationToken).ConfigureAwait(false);
                Assert.Equal(EOutcome.Succeeded, receipt.Outcome);
            }
            string phase = "Replication.Preparing";
            EOutcome expected = EOutcome.Failed;
            if (index != 0)
            {
                phase = "Replication.CommitDecided";
                expected = EOutcome.Succeeded;
            }
            DurableReceipt pending = new(id, receipt!.Fingerprint, EOutcome.Unknown, [], phase);
            Assert.Equal(EOutcome.Succeeded, (await stores[0].ApplyBatchAsync(Guid.NewGuid(), [
                new("cluster:operations", id.ToString("N"), null, NodeValue.FromObject(pending)),
                new("cluster:replication-members", id.ToString("N"), null, NodeValue.FromObject(members)),
            ], cancellationToken).ConfigureAwait(false)).Outcome);
            if (index == 2)
            {
                Assert.Equal(EOutcome.Succeeded, (await stores[0].CommitAsync(id, cancellationToken).ConfigureAwait(false)).Outcome);
            }
            results.Add((id, expected));
        }
        return results;
    }

    /// <summary>現在値の証拠は一致していても旧版が失敗結果を持つ状態を再現します。</summary>
    /// <returns>補完後に再照会する元操作です。</returns>
    private static async Task<Guid> SeedLegacyResultAsync(List<IDurableRepository> stores, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();
        string scope = "workflow:reboot-proof", key = "legacy";
        DurableReceipt success = await stores[0].ApplyBatchAsync(id, [new(scope, key, null, NodeValue.FromObject(42))], cancellationToken).ConfigureAwait(false);
        Infrastructure.Persistence.Documents.RepositorySnapshot current = await stores[0].ReadCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        for (int index = 1; index < stores.Count; index++)
        {
            Assert.True(await stores[index].ImportAsync(new([], [success with { Outcome = EOutcome.Failed, Entries = [], Reason = "Persistence.VersionConflict" }],
                OperationOwners: [new PersistenceOperationOwner(id, success.Fingerprint, [scope])]), cancellationToken).ConfigureAwait(false));
            Assert.True(await stores[index].ImportAsync(new(current.Entries.Where(entry => string.Equals(entry.Scope, scope, StringComparison.Ordinal) && string.Equals(entry.Key, key, StringComparison.Ordinal)).ToArray(), [],
                CausalEntries: current.CausalEntries?.Where(entry => string.Equals(entry.Scope, scope, StringComparison.Ordinal) && string.Equals(entry.Key, key, StringComparison.Ordinal)).ToArray()), cancellationToken).ConfigureAwait(false));
        }
        return id;
    }

    /// <summary>全Coreが現在の実体をReadyとして認識し、背景補完も終わるまで待ちます。</summary>
    /// <returns>各実プロセスの安定した参加確認です。</returns>
    private static Task WaitReadyAsync(IReadOnlyList<CorePeer> peers, CancellationToken cancellationToken = default)
        => CorePeer.WaitAsync(async token =>
        {
            List<Guid> instances = [];
            List<PeerInfo[]> views = [];
            foreach (CorePeer peer in peers)
            {
                JsonElement health = await peer.GetAsync<JsonElement>("health", token).ConfigureAwait(false);
                bool ready = health.GetProperty("ready").GetBoolean();
                JsonElement problem = health.GetProperty("synchronizationProblem");
                int pending = health.GetProperty("historySynchronization").GetProperty("pending").GetInt32();
                if (!ready || problem.ValueKind != JsonValueKind.Null || pending != 0)
                {
                    return false;
                }
                instances.Add(health.GetProperty("instanceId").GetGuid());
                JsonElement status = await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(false);
                views.Add(status.GetProperty("peers").Deserialize<PeerInfo[]>(ContractJson.Options)!);
            }
            return views.All(view => instances.All(instance => view.Any(member => member.InstanceId == instance && member.Status == EPeerStatus.Ready)));
        }, TimeSpan.FromSeconds(90), cancellationToken);

    /// <summary>対象WFの現在の実待受だけを認識します。</summary>
    /// <returns>少なくとも一つの実待受があるかです。</returns>
    private static bool Listening(JsonElement status) => status.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("workflowId").GetString() is "restart-source"
        && source.GetProperty("nodeId").GetString() is "receive" && source.GetProperty("status").GetProperty("state").GetString() is "listening");
}
