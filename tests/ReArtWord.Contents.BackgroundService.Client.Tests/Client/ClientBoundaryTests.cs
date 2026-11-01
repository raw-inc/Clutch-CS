using System.Net;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>遅延応答、受付拒否、送信前保存をC#実装で確認します。</summary>
public sealed class ClientBoundaryTests
{
    /// <summary>他ワークフローへの切替後に到着した定義を適用しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task LateDraftCannotReplaceAnotherWorkflowAsync()
    {
        TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ClientHttpHandler handler = new((_, _) => response.Task);
        using HttpClient client = Client(handler);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WorkflowDefinition definition = Definition();
        await model.LoadAsync(new(definition, ContractJson.Hash(definition), DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        EditorCollaboration collaboration = new(model, new(client));
        using HttpResponseMessage delivered = Reply(new DraftView(definition.Id, null, ContractJson.Hash(definition), definition, [], [], DateTimeOffset.UtcNow));
        Task pending = collaboration.SynchronizeAsync(true, "host");
        try
        {
            model.New();
            string id = model.Draft.Document.Id;
            response.SetResult(delivered);
            await pending.ConfigureAwait(true);
            Assert.Equal(id, model.Draft.Document.Id);
        }
        finally
        {
            response.TrySetCanceled();
            await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>サーバー拒否時に編集を残し、修正後は新しい要求で再確認します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task RejectedInputRemainsEditableAndFixedInputIsSentAsync()
    {
        WorkflowDefinition definition = Definition();
        List<DraftChangeRequest> requests = [];
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            string changes = "/changes";
            if (request.RequestUri!.AbsolutePath.EndsWith(changes, StringComparison.Ordinal))
            {
                DraftChangeRequest change = JsonSerializer.Deserialize<DraftChangeRequest>(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options)!;
                requests.Add(change);
                if (requests.Count == 1)
                {
                    return Reply(new DraftView(definition.Id, null, ContractJson.Hash(definition), definition, [], [new("draft.capacity", "rejected")], DateTimeOffset.UtcNow));
                }
                return Reply(new DraftView(definition.Id, null, ContractJson.Hash(change.Edited), change.Edited, [], [], DateTimeOffset.UtcNow));
            }
            return Reply(Array.Empty<PresenceView>());
        });
        using HttpClient client = Client(handler);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(definition, ContractJson.Hash(definition), DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        model.Change(document => document with { Name = "rejected" });
        EditorCollaboration collaboration = new(model, new(client));
        await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
        Assert.Equal("rejected", model.Draft.Document.Name);
        Assert.Throws<InvalidOperationException>(collaboration.EnsureConfirmed);
        model.Change(document => document with { Name = "fixed" });
        await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
        Assert.Equal(2, requests.Count);
        Assert.NotEqual(requests[0].OperationId, requests[1].OperationId);
        Assert.Equal("fixed", requests[1].Edited.Name);
        collaboration.EnsureConfirmed();
    }

    /// <summary>通信切断でも元の要求は先に保存され、reload後に照会できます。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task MutationRequestIsStoredBeforeTransportFailureAsync()
    {
        BrowserTestRuntime script = new();
        using ClientHttpHandler handler = new((_, _) =>
        {
            Assert.Single(script.Operations);
            throw new HttpRequestException("offline");
        });
        using HttpClient client = Client(handler);
        OperationJournalStorage storage = new(script);
        OperationJournal journal = new(new(client), storage);
        Guid id = Guid.NewGuid();
        await Assert.ThrowsAsync<HttpRequestException>(() => journal.StartAsync(id, "restore", "/api/snapshots/restore", "/api/snapshots/restores/" + id, new { operationId = id, selections = Array.Empty<object>() }, "/api/snapshots/recover")).ConfigureAwait(true);
        OperationJournal reloaded = new(new(client), storage);
        await reloaded.LoadAsync().ConfigureAwait(true);
        Assert.Equal(id, reloaded.Items.Single().OperationId);
        Assert.Equal(id, reloaded.Items.Single().Request.GetProperty("operationId").GetGuid());
        Assert.Equal("unknown", OperationJournal.Outcome(reloaded.Items.Single().Result));
    }

    /// <summary>別更新IDの成功応答と保存不能を成功扱いしません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task WrongOperationAndUnavailableStorageAreNotSuccessAsync()
    {
        int sent = 0;
        using ClientHttpHandler handler = new((_, _) =>
        {
            sent++;
            return Task.FromResult(Reply(new { request = new { operationId = Guid.NewGuid() }, outcome = "succeeded", completed = true }));
        });
        using HttpClient client = Client(handler);
        BrowserTestRuntime script = new();
        OperationJournal journal = new(new(client), new(script));
        Guid id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.StartAsync(id, "update", "/api/updates/start", "/api/updates/" + id, new { operationId = id })).ConfigureAwait(true);
        Assert.Equal("unknown", OperationJournal.Outcome(journal.LastResult));
        script.FailWrites = true;
        await Assert.ThrowsAsync<Microsoft.JSInterop.JSException>(() => journal.StartAsync(Guid.NewGuid(), "update", "/api/updates/start", "/api/updates/new", new { value = 1 })).ConfigureAwait(true);
        Assert.Equal(1, sent);
    }

    /// <summary>画面で別Peerを選んだ後も元操作の照会先を変更しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task OperationQueryKeepsOriginalExplicitPeerAsync()
    {
        Guid id = Guid.NewGuid();
        List<string> peers = [];
        List<string> clusters = [];
        using ClientHttpHandler handler = new((request, _) =>
        {
            peers.Add(request.Headers.GetValues("X-Contents-Peer").Single());
            clusters.Add(request.Headers.GetValues("X-Contents-Cluster").Single());
            return Task.FromResult(Reply(new { operationId = id, outcome = "succeeded" }));
        });
        using HttpClient client = Client(handler);
        ManagementApi api = new(client) { SelectedPeerId = "peer-a", SelectedClusterId = "cluster-a" };
        OperationJournal journal = new(api, new(new BrowserTestRuntime()));
        await journal.StartAsync(id, "restore", "/api/snapshots/restore", "/api/snapshots/restores/" + id, new { operationId = id }).ConfigureAwait(true);
        api.SelectedPeerId = "peer-b";
        api.SelectedClusterId = "cluster-b";
        await journal.ReadAsync(journal.Items.Single()).ConfigureAwait(true);
        await api.GetAsync<JsonElement>("/api/status").ConfigureAwait(true);
        Assert.Equal(["peer-a", "peer-a", "peer-b"], peers);
        Assert.Equal(["cluster-a", "cluster-a", "cluster-b"], clusters);
        Assert.Equal("peer-a", journal.Items.Single().PeerId);
        Assert.Equal("cluster-a", journal.Items.Single().ClusterId);
    }

    /// <summary>選択中の接続先が別でも、取消の要求と照会は元実行のPeerへ送ります。</summary>
    /// <returns>元の取消ID・実行ID・実体・送信先の確認。</returns>
    [Fact]
    public async Task CancellationUsesOriginalRunDestinationAsync()
    {
        Guid id = Guid.NewGuid();
        Guid run = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        int changes = 0;
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            Assert.Equal("original-peer", request.Headers.GetValues("X-Contents-Peer").Single());
            Assert.Equal("original-cluster", request.Headers.GetValues("X-Contents-Cluster").Single());
            if (request.Method == HttpMethod.Post)
            {
                changes++;
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(true));
                Assert.Equal(run, body.RootElement.GetProperty("runId").GetGuid());
                Assert.Equal(owner, body.RootElement.GetProperty("ownerInstanceId").GetGuid());
                Assert.Equal(id, body.RootElement.GetProperty("operationId").GetGuid());
            }
            return Reply(new { operationId = id, runId = run, outcome = "unknown" });
        });
        using HttpClient client = Client(handler);
        ManagementApi api = new(client) { SelectedPeerId = "selected-peer", SelectedClusterId = "selected-cluster" };
        BrowserTestRuntime storage = new();
        OperationJournal journal = new(api, new(storage));
        await journal.StartAsync(id, "cancel", "/api/runs/cancel", "/api/runs/cancellations/" + id, new { operationId = id, runId = run, ownerInstanceId = owner },
            targetPeer: "original-peer", targetCluster: "original-cluster").ConfigureAwait(true);
        OperationJournal restored = new(api, new(storage));
        await restored.LoadAsync().ConfigureAwait(true);
        await restored.ReadAsync(Assert.Single(restored.Items)).ConfigureAwait(true);
        Assert.Equal(1, changes);
        Assert.Equal("unknown", OperationJournal.Outcome(restored.LastResult));
    }

    /// <summary>未到達の明示Peerを既定Coreへ暗黙代替しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task MissingExplicitPeerDoesNotFallbackAsync()
    {
        int requests = 0;
        using ClientHttpHandler handler = new((request, _) =>
        {
            requests++;
            Assert.Equal("missing", request.Headers.GetValues("X-Contents-Peer").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using HttpClient client = Client(handler);
        ManagementApi api = new(client);
        ConnectionState connection = new(api);
        await connection.SelectTargetAsync("cluster-a", "missing").ConfigureAwait(true);
        Assert.False(connection.Connected);
        Assert.Null(connection.Current);
        Assert.Equal("missing", api.SelectedPeerId);
        Assert.Equal(1, requests);
    }

    /// <summary>未確認phaseや全失敗を部分成功と表示しません。</summary>
    [Theory]
    [InlineData("unknown", "succeeded", "unknown")]
    [InlineData("failed", "failed", "failed")]
    [InlineData("succeeded", "failed", "partial")]
    public void SavePhaseOutcomePreservesUnknownAndFailure(string saved, string later, string expected)
    {
        JsonElement result = JsonSerializer.SerializeToElement(new { saved = new { outcome = saved }, shared = new { outcome = later }, snapshot = new { outcome = later }, activation = new { outcome = later } });
        Assert.Equal(expected, OperationJournal.Outcome(result));
    }

    /// <summary>明示した共同編集表示名だけを操作記録ヘッダーへ渡します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ExplicitDisplayNameIsSharedWithRequestMetadataAsync()
    {
        int count = 0;
        using ClientHttpHandler handler = new((request, _) =>
        {
            if (count++ == 0)
            {
                Assert.False(request.Headers.Contains("X-Contents-Display-Name"));
            }
            else
            {
                Assert.Equal("operator", request.Headers.GetValues("X-Contents-Display-Name").Single());
            }
            return Task.FromResult(Reply(new { loaded = true }));
        });
        using HttpClient client = Client(handler);
        ManagementApi api = new(client);
        EditorCollaboration collaboration = new(new(new(new BrowserTestRuntime())), api);
        await api.GetAsync<JsonElement>("/api/status").ConfigureAwait(true);
        collaboration.DisplayName = "operator";
        await api.GetAsync<JsonElement>("/api/status").ConfigureAwait(true);
    }

    /// <summary>同じ共有版の確認で文書を作り直さず、新しい版は通常どおり適用します。</summary>
    /// <returns>共有版の更新境界の確認です。</returns>
    [Fact]
    public async Task UnchangedSharedRevisionDoesNotRebaseTheDocumentAsync()
    {
        WorkflowDefinition definition = Definition();
        DraftView view = new(definition.Id, "base", ContractJson.Hash(definition), definition, [], [], DateTimeOffset.UtcNow);
        using ClientHttpHandler handler = new((request, _) =>
        {
            string suffix = "/draft";
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal) ? Reply(view)
                                                                                                               : Reply(Array.Empty<PresenceView>()));
        });
        using HttpClient client = Client(handler);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(definition, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        EditorCollaboration collaboration = new(model, new(client));
        await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
        EditorDraft accepted = model.Draft;
        int changes = 0;
        model.Changed += () => changes++;
        await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
        Assert.Same(accepted, model.Draft);
        Assert.Equal(0, changes);
        WorkflowDefinition remote = definition with { Name = "remote" };
        view = view with { SessionVersion = ContractJson.Hash(remote), Definition = remote };
        await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
        Assert.Equal("remote", model.Draft.Document.Name);
        Assert.Equal(1, changes);
    }

    /// <summary>一人で静止中の重複通信を抑え、移動があれば次回確認を待たず共有します。</summary>
    /// <returns>入力共有の送信境界です。</returns>
    [Fact]
    public async Task IdlePresenceIsCoalescedButPointerChangesAreSentAsync()
    {
        EditorTestClock clock = new();
        int requests = 0;
        using ClientHttpHandler handler = new((_, _) =>
        {
            requests++;
            return Task.FromResult(Reply(Array.Empty<PresenceView>()));
        });
        using HttpClient client = Client(handler);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        EditorCollaboration collaboration = new(model, new(client, new(clock)));
        await collaboration.SynchronizePresenceAsync("host").ConfigureAwait(true);
        await collaboration.SynchronizePresenceAsync("host").ConfigureAwait(true);
        Assert.Equal(2, requests);
        collaboration.RealtimePointer = true;
        collaboration.SetPointer(new(10, 20), null, null);
        await collaboration.SynchronizePresenceAsync("host").ConfigureAwait(true);
        Assert.Equal(4, requests);
    }

    /// <summary>テスト用のローカルHTTP接続先です。</summary>
    /// <returns>呼出元が所有するクライアントです。</returns>
    private static HttpClient Client(HttpMessageHandler handler) => new(handler, false) { BaseAddress = new Uri("http://localhost/") };

    /// <summary>型付きAPI応答を生成します。</summary>
    /// <returns>受信側が所有する応答です。</returns>
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8, "application/json") };

    /// <summary>独立した空のワークフローです。</summary>
    /// <returns>試験定義です。</returns>
    private static WorkflowDefinition Definition() => new("flow", "original", [], []);
}
