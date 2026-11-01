using System.Net;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>二段tabが所有先や未保存草稿を暗黙変更しないことを確認します。</summary>
public sealed class EditorTabTests
{
    /// <summary>同じタブを再読み込みしても、他クラスタを含む草稿を複製しません。</summary>
    /// <returns>所有者・未保存編集・保存件数の確認です。</returns>
    [Fact]
    public async Task ReloadReusesClaimedStorageWithoutAccumulatingCopies()
    {
        BrowserTestRuntime browser = new() { RetainDraftOwner = true };
        BrowserStorage storage = new(browser);
        EditorDocument model = new(storage);
        await model.SwitchTargetAsync("cluster-one", "peer").ConfigureAwait(true);
        model.New();
        model.Change(document => document with { Name = "unsaved one" });
        string first = model.Draft.Document.Id;
        await model.SwitchTargetAsync("cluster-two", "peer").ConfigureAwait(true);
        model.New();
        model.Change(document => document with { Name = "unsaved two" });
        await model.PersistAsync().ConfigureAwait(true);
        Guid owner = model.StorageOwner;
        int records = browser.Local.Count;
        for (int iteration = 0; iteration < 5; iteration++)
        {
            model = new(storage);
            await model.InitializeAsync().ConfigureAwait(true);
            Assert.Equal(owner, model.StorageOwner);
            await model.SwitchTargetAsync("cluster-one", "peer").ConfigureAwait(true);
            Assert.True(await model.TryOpenDraftAsync(first).ConfigureAwait(true));
            Assert.Equal("unsaved one", model.Draft.Document.Name);
            Assert.True(model.Draft.Dirty);
            await model.SwitchTargetAsync("cluster-two", "peer").ConfigureAwait(true);
            Assert.Equal("unsaved two", model.Draft.Document.Name);
            Assert.Equal(records, browser.Local.Count);
        }
    }

    /// <summary>削除済み草稿は再読込で戻らず、他の未保存草稿を維持します。</summary>
    /// <returns>草稿索引・tabの削除範囲です。</returns>
    [Fact]
    public async Task ExplicitDeletionForgetsOnlyTheSelectedDraft()
    {
        BrowserTestRuntime browser = new();
        BrowserStorage storage = new(browser);
        EditorDocument model = new(storage);
        await model.InitializeAsync().ConfigureAwait(true);
        model.New();
        string first = model.Draft.Document.Id;
        await model.PersistAsync().ConfigureAwait(true);
        model.New();
        string deleted = model.Draft.Document.Id;
        await model.PersistAsync().ConfigureAwait(true);
        await model.DiscardAsync().ConfigureAwait(true);
        Assert.False(model.HasDocument);
        await model.PersistAsync().ConfigureAwait(true);
        EditorDocument reloaded = new(storage);
        await reloaded.InitializeAsync().ConfigureAwait(true);
        Assert.False(reloaded.HasDocument);
        Assert.Single(await EditorDraftStorage.ReadAllAsync(storage, reloaded.StorageOwner, null, null).ConfigureAwait(true));
        Assert.False(await reloaded.TryOpenDraftAsync(deleted).ConfigureAwait(true));
        Assert.True(await reloaded.TryOpenDraftAsync(first).ConfigureAwait(true));
        EditorWorkflowTabState tabs = new();
        tabs.Update([], deleted, "deleted", true, false);
        tabs.Forget(deleted);
        Assert.Single(tabs.Update([], first, "kept", true, false));
    }

    /// <summary>前回のPeerを優先し、Coordinator候補が複数なら一つを捏造しません。</summary>
    [Fact]
    public void ClusterSuggestionRequiresExplicitUnambiguousPeer()
    {
        PeerInfo first = Peer("a");
        PeerInfo second = Peer("b");
        DiscoveredCluster cluster = new("cluster", [first, second], ["a"], DateTimeOffset.UtcNow);
        Assert.Equal(second, EditorClusterChoice.Suggest(cluster, "b"));
        Assert.Null(EditorClusterChoice.Suggest(cluster with { Peers = [first, second with { Status = EPeerStatus.Unavailable }] }, "b"));
        Assert.Null(EditorClusterChoice.Suggest(cluster, "missing"));
        Assert.Equal(first, EditorClusterChoice.Suggest(cluster, null));
        Assert.Null(EditorClusterChoice.Suggest(cluster with { CoordinatorCandidates = ["a", "b"] }, null));
        Assert.Null(EditorClusterChoice.Suggest(cluster with { Peers = [first], CoordinatorCandidates = [] }, null));
        Assert.Equal(first, EditorClusterChoice.Suggest(cluster with { Peers = [first], CoordinatorCandidates = [] }, "a"));
        Assert.False(EditorClusterChoice.IsSameInstance([cluster], "cluster", "a", Guid.NewGuid()));
        Assert.False(EditorClusterChoice.IsSameInstance([cluster with { Peers = [first, first with { InstanceId = Guid.NewGuid() }] }], "cluster", "a", first.InstanceId));
    }

    /// <summary>未保存新規tabはページ切替後も残り、保存済みになれば未保存表示を解除します。</summary>
    [Fact]
    public void UnsavedWorkflowTabSurvivesAnotherSelection()
    {
        EditorWorkflowTabState tabs = new();
        tabs.Update([], "one", "Unsaved one", true, false);
        IReadOnlyList<EditorWorkflowTab> next = tabs.Update([], "two", "Local two", false, true);
        Assert.True(next.Single(item => string.Equals(item.Id, "one", StringComparison.Ordinal)).Dirty);
        Assert.True(next.Single(item => string.Equals(item.Id, "two", StringComparison.Ordinal)).LocalOnly);
        next = tabs.Update([], "one", "Saved one", false, false);
        Assert.False(next.Single(item => string.Equals(item.Id, "one", StringComparison.Ordinal)).Dirty);
    }

    /// <summary>新規草稿を同じowner内で復元し、別Peerや保存済みワークフローの読取を迂回しません。</summary>
    /// <returns>未確定入力も失わない復元です。</returns>
    [Fact]
    public async Task NewDraftReopensOnlyWithinItsOwnerAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        EditorDocument model = new(storage);
        await model.InitializeAsync().ConfigureAwait(true);
        await model.SwitchTargetAsync("cluster", "a").ConfigureAwait(true);
        model.New();
        string id = model.Draft.Document.Id;
        model.Change(value => value with { Name = "local draft" });
        model.SetInput("node/value", "{unfinished");
        await model.PersistAsync().ConfigureAwait(true);
        model.New();
        Assert.True(await model.TryOpenDraftAsync(id).ConfigureAwait(true));
        Assert.Equal("local draft", model.Draft.Document.Name);
        Assert.Equal("{unfinished", model.ReadInput("node/value"));
        await model.SwitchTargetAsync("cluster", "b").ConfigureAwait(true);
        Assert.False(await model.TryOpenDraftAsync(id).ConfigureAwait(true));
        model.New();
        StoredWorkflow saved = new(model.Draft.Document, "saved", DateTimeOffset.UtcNow, EActivationPolicy.Auto);
        await model.LoadAsync(saved).ConfigureAwait(true);
        Assert.False(await model.TryOpenDraftAsync(saved.Definition.Id).ConfigureAwait(true));
    }

    /// <summary>実体確認・草稿保存・ブラウザー選択保存より先に新Peerへ照会しません。</summary>
    /// <returns>同じ実体だけに向いた選択順の確認です。</returns>
    [Fact]
    public async Task TargetSelectionPreservesDraftBeforeChangingHeadersAsync()
    {
        BrowserTestRuntime browser = new();
        BrowserStorage storage = new(browser);
        EditorDocument model = new(storage);
        await model.InitializeAsync().ConfigureAwait(true);
        PeerInfo peer = Peer("peer");
        model.New();
        DiscoveredCluster cluster = new("cluster", [peer], [peer.PeerId], DateTimeOffset.UtcNow);
        int statusReads = 0;
        using ClientHttpHandler handler = new((request, _) =>
        {
            string path = "/client/clusters";
            if (string.Equals(request.RequestUri!.AbsolutePath, path, StringComparison.Ordinal))
            {
                return Task.FromResult(Response(new[] { cluster }));
            }
            statusReads++;
            Assert.Equal("cluster", model.ClusterId);
            Assert.Equal("peer", model.PeerId);
            Assert.Equal("\"cluster\"", browser.Session["contents.client.cluster"]);
            Assert.Equal("\"peer\"", browser.Session["contents.client.peer"]);
            return Task.FromResult(Response(new { peerId = "peer", instanceId = peer.InstanceId, status = "ready", peers = Array.Empty<object>(), sources = Array.Empty<object>(), runningNodes = Array.Empty<Guid>(), maintenance = new { } }));
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://127.0.0.1") };
        ManagementApi api = new(client);
        ConnectionState connection = new(api);
        using EditorTargetSelection selection = new(model, storage, connection, api);
        await selection.SelectClusterAsync("cluster", "peer", peer.InstanceId).ConfigureAwait(true);
        Assert.Null(selection.PreviousPeer("cluster"));
        PeerInfo coordinator = Peer("next");
        Assert.Equal(coordinator, EditorClusterChoice.Suggest(cluster with { Peers = [peer, coordinator], CoordinatorCandidates = ["next"] }, selection.PreviousPeer("cluster")));
        await selection.SelectAsync("cluster", "peer", peer.InstanceId).ConfigureAwait(true);
        Assert.Equal("peer", selection.PreviousPeer("cluster"));
        Assert.Equal(2, statusReads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => selection.SelectAsync("cluster", "peer", Guid.NewGuid())).ConfigureAwait(true);
        model.Change(value => value with { Name = "unsaved" });
        browser.FailWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => selection.SelectAsync("cluster", "peer", peer.InstanceId)).ConfigureAwait(true);
        Assert.Equal(2, statusReads);
        Assert.Equal("unsaved", model.Draft.Document.Name);
    }

    /// <summary>古い候補照会の完了を、切替後の新しい草稿へ適用しません。</summary>
    /// <returns>遅延応答の世代境界です。</returns>
    [Fact]
    public async Task LateClusterObservationDoesNotReplaceNewDraftAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        EditorDocument model = new(storage);
        await model.InitializeAsync().ConfigureAwait(true);
        PeerInfo peer = Peer("peer");
        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ClientHttpHandler handler = new((_, _) =>
        {
            observed.SetResult();
            return reply.Task;
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://127.0.0.1") };
        ManagementApi api = new(client);
        using EditorTargetSelection selection = new(model, storage, new(api), api);
        using HttpResponseMessage received = Response(new[] { new DiscoveredCluster("cluster", [peer], [peer.PeerId], DateTimeOffset.UtcNow) });
        Task changing = selection.SelectAsync("cluster", "peer", peer.InstanceId);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            model.New();
            string current = model.Draft.Document.Id;
            reply.SetResult(received);
            await Assert.ThrowsAsync<InvalidOperationException>(() => changing).ConfigureAwait(true);
            Assert.Null(api.SelectedClusterId);
            Assert.Equal(current, model.Draft.Document.Id);
        }
        finally
        {
            reply.TrySetCanceled();
            await changing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>既定英語と明示日本語で表示だけを切り替えます。</summary>
    /// <returns>画面語彙の自然な切替です。</returns>
    [Fact]
    public async Task EditorVocabularyUsesEnglishUntilJapaneseIsSelectedAsync()
    {
        BrowserTestRuntime browser = new();
        ReArtWord.Contents.BackgroundService.Client.Localization.UiText text = new(new(browser));
        await text.InitializeAsync().ConfigureAwait(true);
        Assert.Equal("Choose peer", text["Choose peer"]);
        Assert.Equal("en", browser.DocumentLanguage);
        NodeDescriptor node = new("http.listen", "raw name", "Communication", "raw description", ENodeKind.Source, EExecutionLocation.ExplicitPeer, [], [], [], JsonSerializer.Deserialize<JsonElement>("{}"));
        Assert.Equal("HTTP server", text.NodeName(node));
        Assert.Equal("Convert scalar", text.NodeName(node with { Type = "data.convert" }));
        Assert.Equal("Add", text.NodeName(node with { Type = "math.add" }));
        Assert.Equal("Compare", text.NodeName(node with { Type = "math.compare" }));
        Assert.Equal("Is Equal", text.NodeName(node with { Type = "math.equal" }));
        Assert.Equal("Is Not Equal", text.NodeName(node with { Type = "math.notEqual" }));
        Assert.Equal("Power", text.NodeName(node with { Type = "math.power" }));
        Assert.Equal("Root", text.NodeName(node with { Type = "math.root" }));
        Assert.Equal("AND", text.NodeName(node with { Type = "math.and" }));
        Assert.Equal("OR", text.NodeName(node with { Type = "math.or" }));
        Assert.Equal("NOT", text.NodeName(node with { Type = "math.not" }));
        Assert.Equal("NAND", text.NodeName(node with { Type = "math.nand" }));
        Assert.Equal("NOR", text.NodeName(node with { Type = "math.nor" }));
        Assert.Equal("XOR", text.NodeName(node with { Type = "math.xor" }));
        Assert.Equal("ANDS", text.NodeName(node with { Type = "math.ands" }));
        Assert.Equal("ORS", text.NodeName(node with { Type = "math.ors" }));
        Assert.Equal("≤ · Less than or equal", text["lessOrEqual"]);
        Assert.Equal("0 explicitly means no time limit.", text["0は明示無期限"]);
        await text.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("ja", browser.DocumentLanguage);
        Assert.Equal("ピアを選択", text["Choose peer"]);
        Assert.Equal("HTTP サーバー", text.NodeName(node));
        Assert.Equal("加算", text.NodeName(node with { Type = "math.add" }));
        Assert.Equal("大小比較", text.NodeName(node with { Type = "math.compare" }));
        Assert.Equal("一致判定", text.NodeName(node with { Type = "math.equal" }));
        Assert.Equal("不一致判定", text.NodeName(node with { Type = "math.notEqual" }));
        Assert.Equal("累乗", text.NodeName(node with { Type = "math.power" }));
        Assert.Equal("累乗根", text.NodeName(node with { Type = "math.root" }));
        Assert.Equal("論理積 (AND)", text.NodeName(node with { Type = "math.and" }));
        Assert.Equal("論理和 (OR)", text.NodeName(node with { Type = "math.or" }));
        Assert.Equal("否定 (NOT)", text.NodeName(node with { Type = "math.not" }));
        Assert.Equal("否定論理積 (NAND)", text.NodeName(node with { Type = "math.nand" }));
        Assert.Equal("否定論理和 (NOR)", text.NodeName(node with { Type = "math.nor" }));
        Assert.Equal("排他的論理和 (XOR)", text.NodeName(node with { Type = "math.xor" }));
        Assert.Equal("配列の論理積 (ANDS)", text.NodeName(node with { Type = "math.ands" }));
        Assert.Equal("配列の論理和 (ORS)", text.NodeName(node with { Type = "math.ors" }));
        Assert.Equal("≤ · 以下", text["lessOrEqual"]);
        Assert.Equal("変換先の型", text.SettingName("targetType"));
        Assert.Equal("raw name", text.NodeName(node with { Type = "extension.http" }));
        Assert.Equal("通信", text[GraphPresentation.CategoryName("Communication")]);
        await text.ChangeAsync("en").ConfigureAwait(true);
        Assert.Equal("Communication", text[GraphPresentation.CategoryName("Communication")]);
    }

    /// <summary>候補に独立した生存実体を割り当てます。</summary>
    /// <returns>表示観測です。</returns>
    private static PeerInfo Peer(string id) => new(id, Guid.NewGuid(), id, "127.0.0.1", 4000, EPeerStatus.Ready, 1, null, DateTimeOffset.UtcNow);

    /// <summary>公開JSON契約で応答します。</summary>
    /// <returns>呼出元が解放するHTTP応答です。</returns>
    private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8, "application/json") };
}
