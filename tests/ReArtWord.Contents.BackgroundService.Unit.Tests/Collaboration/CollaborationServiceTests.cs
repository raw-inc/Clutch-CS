using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;

/// <summary>未保存編集とブラウザごとの一時表示を検証します。</summary>
public sealed class CollaborationServiceTests : IDisposable
{
    private readonly MediaTestFiles files = new();

    /// <summary>新規の不完全graphを共有し、保存・effectiveは作りません。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task IncompleteNewDraftMergesOfflineEditsWithoutSaving()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Cluster(repository);
        await cluster.StartAsync();
        WorkflowStore store = new(repository, cluster, new WorkflowCompiler(new NodeRegistry([])));
        CollaborationService service = new(store, cluster);
        WorkflowDefinition original = new("new", "blank", [], []);
        WorkflowDefinition incomplete = original with { Wires = [new WireDefinition("missingA", "out", "missingB", "in")] };
        DraftView first = await service.ApplyChangesAsync(Change(original, incomplete) with { BaseSessionVersion = null });
        Assert.Empty(first.Validation);
        Assert.Single(first.Definition.Wires);
        DraftView next = await service.ApplyChangesAsync(Change(original, original with { Name = "renamed" }));
        Assert.Empty(next.Conflicts);
        Assert.Equal("renamed", next.Definition.Name);
        Assert.Single(next.Definition.Wires);
        Assert.Null(await store.GetAsync("new"));
        Assert.Empty(await repository.ListAsync("workflow.activation"));
        Assert.Empty(await repository.ListAsync("receipts:one"));
    }

    /// <summary>同項目の複数候補を保持し、元版不一致とID使い回しを拒否します。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task ConflictsRequireResolutionAndCannotOverwriteKnownBase()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Cluster(repository);
        await cluster.StartAsync();
        CollaborationService service = new(new WorkflowStore(repository, cluster, new WorkflowCompiler(new NodeRegistry([]))), cluster);
        WorkflowDefinition original = new("demo", "original", [], []);
        await service.ApplyChangesAsync(Change(original, original with { Name = "first" }));
        DraftChangeRequest second = Change(original, original with { Name = "second" });
        DraftView conflict = await service.ApplyChangesAsync(second);
        Assert.Single(conflict.Conflicts);
        Assert.Equal("first", conflict.Definition.Name);
        DraftView third = await service.ApplyChangesAsync(Change(original, original with { Name = "third" }));
        Assert.Equal(2, third.Conflicts.Count);
        DraftView mismatch = await service.ApplyChangesAsync(second with { Edited = original with { Name = "different" } });
        Assert.Equal("draft.operationMismatch", Assert.Single(mismatch.Validation).Code);
        DraftView wrongBase = await service.ApplyChangesAsync(Change(original, original with { Enabled = true }) with { BaseSessionVersion = conflict.SessionVersion });
        Assert.Equal("draft.baseMismatch", Assert.Single(wrongBase.Validation).Code);
        DraftView resolved = await service.ApplyChangesAsync(second with { OperationId = Guid.NewGuid(), Resolutions = new Dictionary<string, EEditResolution>(StringComparer.Ordinal) { ["/name"] = EEditResolution.UseEdited } });
        Assert.Empty(resolved.Conflicts);
        Assert.Equal("second", resolved.Definition.Name);
    }

    /// <summary>参加表示の照会中に日時が補正されても未保存草稿を削除せず、実際の無参照期間だけで回収します。</summary>
    /// <returns>参加表示と草稿の独立した寿命を確認します。</returns>
    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public async Task ClockCorrectionAndPresenceExpiryDoNotDiscardUnsavedDraftsAsync(int offsetDays)
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Cluster(repository);
        await cluster.StartAsync();
        CollaborationClock clock = new();
        CollaborationService service = new(new WorkflowStore(repository, cluster, new WorkflowCompiler(new NodeRegistry([]))), cluster, clock,
            new() { PresenceLifetime = TimeSpan.FromSeconds(10), DraftLifetime = TimeSpan.FromHours(1) });
        WorkflowDefinition original = new("draft", "Original", [], []);
        DraftView draft = await service.ApplyChangesAsync(Change(original, original with { Name = "Unsaved" }));
        await service.UpdatePresenceAsync(new(original.Id, Guid.NewGuid(), "host", "Editor", [], Dirty: true));
        clock.Adjust(TimeSpan.FromDays(offsetDays));
        Assert.Single(service.GetPresence(original.Id));
        Assert.Equal(draft.SessionVersion, service.CaptureDraft(original.Id)!.SessionVersion);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Empty(service.GetPresence(original.Id));
        Assert.Equal(draft.SessionVersion, service.CaptureDraft(original.Id)!.SessionVersion);
        Assert.Null(await repository.ReadAsync("workflows", original.Id).ConfigureAwait(true));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(service.GetPresence(original.Id));
        Assert.Null(service.CaptureDraft(original.Id));
    }

    /// <summary>同じ人の複数UI実体は混ざらず、期限で表示から退場します。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task PresenceSeparatesUiInstancesAndExpires()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Cluster(repository);
        await cluster.StartAsync();
        CollaborationClock clock = new();
        CollaborationService service = new(new WorkflowStore(repository, cluster, new WorkflowCompiler(new NodeRegistry([]))), cluster, clock, new CollaborationOptions { PresenceLifetime = TimeSpan.FromSeconds(10), MaximumPresence = 2 });
        PresenceUpdate first = new("demo", Guid.NewGuid(), "host", "editor", ["nodeA"], 1, 2);
        await service.UpdatePresenceAsync(first);
        await service.UpdatePresenceAsync(first with { UiInstanceId = Guid.NewGuid(), SelectedNodeIds = ["nodeB"] });
        Assert.Equal(2, service.GetPresence("demo").Count);
        Assert.All(service.GetPresence("demo"), item => Assert.Equal(cluster.LocalInstanceId, item.PeerInstanceId));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Empty(service.GetPresence("demo"));
    }

    /// <summary>退場した下書きと上限超過のUIを有限保持にします。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task DraftRetentionAndPresenceCapacityStayBounded()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Cluster(repository);
        await cluster.StartAsync();
        CollaborationClock clock = new();
        CollaborationService service = new(new WorkflowStore(repository, cluster, new WorkflowCompiler(new NodeRegistry([]))), cluster, clock, new CollaborationOptions { DraftLifetime = TimeSpan.FromSeconds(10), MaximumDrafts = 1, MaximumPresence = 1 });
        WorkflowDefinition first = new("first", "new", [], []);
        WorkflowDefinition second = first with { Id = "second" };
        await service.ApplyChangesAsync(Change(first, first with { Name = "edited" }));
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.ApplyChangesAsync(Change(second, second with { Name = "edited" }));
        Assert.Null(await service.GetDraftAsync("first"));
        Assert.NotNull(await service.GetDraftAsync("second"));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(await service.GetDraftAsync("second"));
        PresenceUpdate presence = new("first", Guid.NewGuid(), "host", "one", []);
        await service.UpdatePresenceAsync(presence);
        clock.Advance(TimeSpan.FromSeconds(1));
        PresenceUpdate newer = presence with { UiInstanceId = Guid.NewGuid(), DisplayName = "two" };
        await service.UpdatePresenceAsync(newer);
        Assert.Equal(newer.UiInstanceId, Assert.Single(service.GetPresence("first")).Presence.UiInstanceId);
    }

    /// <summary>操作せず閲覧中でも未保存本文を維持し、参照が止まってから期限で解除します。</summary>
    /// <returns>版番号を増やさない保持期限の確認です。</returns>
    [Fact]
    public async Task ReadingAnUnchangedDraftKeepsUnsavedEditsAlive()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Cluster(repository);
        await cluster.StartAsync();
        CollaborationClock clock = new();
        CollaborationService service = new(new WorkflowStore(repository, cluster, new WorkflowCompiler(new NodeRegistry([]))), cluster,
            clock, new CollaborationOptions { DraftLifetime = TimeSpan.FromSeconds(10) });
        WorkflowDefinition original = new("new", "initial", [], []);
        DraftView edited = await service.ApplyChangesAsync(Change(original, original with { Name = "unsaved" }));
        for (int pass = 0; pass < 3; pass++)
        {
            clock.Advance(TimeSpan.FromSeconds(9));
            DraftView? current = await service.GetDraftAsync(original.Id);
            Assert.NotNull(current);
            Assert.Equal(edited.SessionVersion, current.SessionVersion);
            Assert.Equal("unsaved", current.Definition.Name);
        }
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(await service.GetDraftAsync(original.Id));
    }

    /// <summary>通知を取りこぼしたPeerもGETの再照会で未保存候補を取得します。</summary>
    /// <returns>実UDPでの検証完了。</returns>
    [Fact]
    public async Task ReconnectFetchesMissedDraftOverUdp()
    {
        int port = Port();
        string id = Guid.NewGuid().ToString("N");
        SqliteRepository firstRepository = Repository();
        SqliteRepository secondRepository = Repository();
        await using PeerCluster first = Cluster(firstRepository, id, "first", port);
        await first.StartAsync();
        CollaborationService firstService = new(new WorkflowStore(firstRepository, first, new WorkflowCompiler(new NodeRegistry([]))), first);
        WorkflowDefinition original = new("new", "initial", [], []);
        await firstService.ApplyChangesAsync(Change(original, original with { Name = "offline" }));
        await using PeerCluster second = Cluster(secondRepository, id, "second", port, false);
        CollaborationService secondService = new(new WorkflowStore(secondRepository, second, new WorkflowCompiler(new NodeRegistry([]))), second);
        await second.StartAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (second.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, timeout.Token);
        }
        DraftView? recovered = null;

        // 照会が戻っても未受信の場合があるため、草稿の到着を確認します。
        while (recovered is null)
        {
            recovered = await secondService.GetDraftAsync("new", timeout.Token).ConfigureAwait(true);
            if (recovered is null)
            {
                await Task.Delay(20, timeout.Token).ConfigureAwait(true);
            }
        }
        Assert.Equal("offline", recovered.Definition.Name);
        Assert.Null(await secondRepository.ReadAsync("workflows", "new"));
        PresenceUpdate presence = new("new", Guid.NewGuid(), "secondHost", "user", ["selected"]);
        await secondService.UpdatePresenceAsync(presence);
        string workflowId = "new";
        while (firstService.GetPresence(workflowId).Count == 0)
        {
            await Task.Delay(20, timeout.Token);
        }
        PresenceView remote = Assert.Single(firstService.GetPresence("new"));
        Assert.Equal("second", remote.PeerId);
        Assert.Equal(second.LocalInstanceId, remote.PeerInstanceId);
    }

    /// <summary>実UDPで別Coreへ届いた参加表示は、両者の日時が違っても受信後の経過時間で失効します。</summary>
    /// <returns>期限後の新しい在席通知による復帰も確認します。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task PresenceLifetimeSurvivesClockSkewAcrossUdpPeersAsync(int offsetSeconds)
    {
        int port = Port();
        string id = Guid.NewGuid().ToString("N");
        SqliteRepository firstRepository = Repository(), secondRepository = Repository();
        await using PeerCluster first = Cluster(firstRepository, id, "first", port);
        await using PeerCluster second = Cluster(secondRepository, id, "second", port, false);
        CollaborationClock firstClock = new(), secondClock = new();
        firstClock.Adjust(TimeSpan.FromSeconds(offsetSeconds));
        secondClock.Adjust(TimeSpan.FromSeconds(-offsetSeconds));
        CollaborationService firstService = new(new(firstRepository, first, new WorkflowCompiler(new NodeRegistry([]))), first, firstClock);
        CollaborationService secondService = new(new(secondRepository, second, new WorkflowCompiler(new NodeRegistry([]))), second, secondClock);
        await first.StartAsync();
        await second.StartAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (second.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        PresenceUpdate update = new("clocked", Guid.NewGuid(), "host", "Editor", []);
        await firstService.UpdatePresenceAsync(update, timeout.Token).ConfigureAwait(true);
        while (!secondService.GetPresence(update.WorkflowId).Any(item => item.Sequence is not null))
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal(TimeSpan.FromSeconds(45), Assert.Single(secondService.GetPresence(update.WorkflowId)).Remaining);
        secondClock.Adjust(TimeSpan.FromDays(2));
        Assert.Single(secondService.GetPresence(update.WorkflowId));
        secondClock.Advance(TimeSpan.FromSeconds(46));
        Assert.Empty(secondService.GetPresence(update.WorkflowId));
        await firstService.UpdatePresenceAsync(update, timeout.Token).ConfigureAwait(true);
        while (secondService.GetPresence(update.WorkflowId).Count == 0)
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal(TimeSpan.FromSeconds(45), Assert.Single(secondService.GetPresence(update.WorkflowId)).Remaining);
    }

    /// <summary>編集元の内容識別子を添えて変更を作ります。</summary>
    /// <returns>未保存編集要求。</returns>
    private static DraftChangeRequest Change(WorkflowDefinition original, WorkflowDefinition edited) => new(Guid.NewGuid(), original.Id, Guid.NewGuid(), null, ContractJson.Hash(original), original, edited, new Dictionary<string, EEditResolution>(StringComparer.Ordinal));

    /// <summary>独立した保存先です。</summary>
    /// <returns>SQLite保存先。</returns>
    private SqliteRepository Repository() => new(Path.Combine(files.Root, Guid.NewGuid().ToString("N"), "state.db"));

    /// <summary>明示ループバックのPeerを構成します。</summary>
    /// <returns>試験Peer。</returns>
    private PeerCluster Cluster(IDurableRepository repository, string? id = null, string peerId = "one", int? port = null, bool initialize = true) => new(new ClusterOptions(id ?? Guid.NewGuid().ToString("N"), peerId, "host", "127.0.0.1", "127.255.255.255", port ?? Port(), 1, initialize, files.Root) { HeartbeatInterval = TimeSpan.FromMilliseconds(50) }, repository) { ReadinessCheck = token => ValueTask.FromResult(true) };

    /// <summary>利用可能な試験ポートを割り当てます。</summary>
    /// <returns>UDP port。</returns>
    private static int Port()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    /// <summary>所有Peerの終了後に、試験用の保存先を削除します。</summary>
    public void Dispose() => files.Dispose();
}
