using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>タブ順、カードの公開情報、元WFへの操作対象を検証します。</summary>
public sealed class WfDashboardClientTests
{
    /// <summary>コメントは非編集中に入力欄を出さず、HTMLを安全な文章として表示します。</summary>
    /// <returns>実Razorの表示確認です。</returns>
    [Fact]
    public async Task CommentsRenderAsPlainTextUntilEditedAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        EditorDocument model = new(storage);
        await model.SwitchTargetAsync("cluster", "peer").ConfigureAwait(true);
        model.New(dashboard: true);
        NodeDefinition note = new("note", WfDashboardLayout.CommentType, WfDashboardLayout.CommentSettings("運用メモ\n<script>content</script>"));
        model.Change(document => document with { Nodes = [note] });
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new UiText(storage));
        services.AddSingleton(model);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<WfDashboardComment>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(WfDashboardComment.Node)] = note,
        })).ConfigureAwait(true)).ToHtmlString()).ConfigureAwait(true);
        Assert.DoesNotContain("<textarea", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<input", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;content&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("運用メモ", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
    }

    /// <summary>初回は左端、手動配置後はWFと混在する順序を復元します。</summary>
    [Fact]
    public void DefaultsDashboardsToTheLeftAndPreservesManualMixedOrder()
    {
        EditorWorkflowTabState state = new();
        WorkflowListSummary[] items = [Summary("flow"), Summary("sub" ) with { IsSubworkflow = true }, Summary("board") with { IsDashboard = true }];
        Assert.Equal(["board", "flow", "sub"], state.Update(items, "flow", "Flow", false, false).Select(tab => tab.Id));
        state.Move("board", "sub");
        EditorWorkflowTabState restored = new();
        restored.Restore(ContractJson.Copy(state.Export()));
        Assert.Equal(["flow", "sub", "board"], restored.Update(items, "flow", "Flow", false, false).Select(tab => tab.Id));
        Assert.True(Assert.Single(restored.All, tab => tab.Id is "board").IsDashboard);
        Assert.Equal(["new-board", "flow", "sub", "board"], restored.Update([.. items, Summary("new-board") with { IsDashboard = true }], "flow", "Flow", false, false).Select(tab => tab.Id));
        Assert.Equal("sub", restored.NextVisible("board"));
        restored.Forget("board");
        Assert.Equal("new-board", restored.NextVisible("board"));
    }

    /// <summary>WFDashboard草稿は再読み込みでき、同じIDの種類変更は拒否します。</summary>
    /// <returns>保存と取込の種類境界です。</returns>
    [Fact]
    public async Task DashboardDraftKeepsItsKindAcrossReloadAndImportAsync()
    {
        BrowserTestRuntime browser = new() { RetainDraftOwner = true };
        BrowserStorage storage = new(browser);
        EditorDocument model = new(storage);
        await model.SwitchTargetAsync("cluster", "peer").ConfigureAwait(true);
        model.New(dashboard: true);
        model.Change(value => value with { Name = "Controls" });
        await model.PersistAsync().ConfigureAwait(true);
        EditorDocument restored = new(storage);
        await restored.SwitchTargetAsync("cluster", "peer").ConfigureAwait(true);
        Assert.True(restored.Draft.Document.IsDashboard);
        Assert.Equal("Controls", restored.Draft.Document.Name);
        Assert.Throws<InvalidOperationException>(() => restored.Import(restored.Draft.Document with { IsDashboard = false }));
        Assert.Throws<InvalidOperationException>(() => restored.Change(value => value with { IsDashboard = false }));
    }

    /// <summary>実Razorのカードは型変更欄・接続口・後続値を出さず、描画だけで操作しません。</summary>
    /// <returns>Value入力またはボタンだけを持つHTMLです。</returns>
    [Theory]
    [InlineData("value.constant", true)]
    [InlineData("control.button", true)]
    [InlineData("control.toggleButton", true)]
    [InlineData("control.toggleButton", false)]
    [InlineData("control.toggleButton", null)]
    public async Task RendersCompactControlsWithoutPortOrTypeEditingAsync(string type, bool? toggle)
    {
        WfDashboardReference reference = new("source", "node", type);
        WfDashboardNodeState state = new(reference, "Source", "Cue", "revision", true, Label: "Go <now>", ValueText: "42", ValueType: "integer");
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        int pressed = 0;
        string html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<WfDashboardCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Reference"] = reference, ["State"] = state, ["Available"] = true, ["Toggle"] = toggle,
            ["Pressed"] = EventCallback.Factory.Create(this, () => pressed++),
        })).ConfigureAwait(true)).ToHtmlString()).ConfigureAwait(true);
        Assert.DoesNotContain("<select", html, StringComparison.Ordinal);
        Assert.DoesNotContain("node-port", html, StringComparison.Ordinal);
        Assert.Equal(0, pressed);
        string valueType = "value.constant";
        if (string.Equals(type, valueType, StringComparison.Ordinal))
        {
            Assert.Contains("value=\"42\"", html, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Go &lt;now&gt;", html, StringComparison.Ordinal);
        }
        string toggleType = "control.toggleButton";
        if (string.Equals(type, toggleType, StringComparison.Ordinal))
        {
            if (toggle is { } known)
            {
                Assert.Contains("role=\"switch\"", html, StringComparison.Ordinal);
                string expected = "aria-checked=\"false\"";
                if (known)
                {
                    expected = "aria-checked=\"true\"";
                }
                Assert.Contains(expected, html, StringComparison.Ordinal);
                Assert.DoesNotContain("disabled", html, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("disabled", html, StringComparison.Ordinal);
                Assert.Contains("Unknown", html, StringComparison.Ordinal);
                Assert.DoesNotContain("aria-checked", html, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>押下は元WFの保存版とノードIDを送り、WFDashboard自身は実行しません。</summary>
    /// <returns>HTTPに渡された操作と接続先変更時の送信拒否です。</returns>
    [Fact]
    public async Task PressUsesSourceIdentityAndRefusesChangedConnectionAsync()
    {
        List<WorkflowRunRequest> requests = [];
        using ClientHttpHandler handler = new(async (message, token) =>
        {
            WorkflowRunRequest request = JsonSerializer.Deserialize<WorkflowRunRequest>(await message.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options)!;
            requests.Add(request);
            WorkflowRunStatus result = new(request.OperationId, "peer", request.WorkflowId, request.Revision!, EOutcome.Unknown, true);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result, ContractJson.Options), Encoding.UTF8, "application/json") };
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://127.0.0.1") };
        ManagementApi api = new(client) { SelectedClusterId = "cluster", SelectedPeerId = "peer" };
        OperationJournal journal = new(api, new OperationJournalStorage(new BrowserTestRuntime()));
        WfDashboardActions actions = new(api, journal);
        WfDashboardNodeState state = new(new("source", "toggle", "control.toggleButton"), "Source", "Toggle", "confirmed", true);
        await actions.PressAsync("cluster", "peer", state).ConfigureAwait(true);
        WorkflowRunRequest sent = Assert.Single(requests);
        Assert.Equal("source", sent.WorkflowId);
        Assert.Equal("toggle", sent.EntryNodeId);
        Assert.True(sent.Inputs!["trigger"].Json.GetBoolean());
        api.SelectedClusterId = "other";
        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.PressAsync("cluster", "peer", state)).ConfigureAwait(true);
        Assert.Single(requests);
    }

    /// <summary>カード内のPreviewとProgressは、元クラスタの本文を読み実際に描画します。</summary>
    /// <returns>参照パラメーター、本文取得、コンパクト表示の確認です。</returns>
    [Theory]
    [InlineData("control.preview", "12")]
    [InlineData("control.progress", "{\"Total\":100,\"Current\":12}")]
    public async Task CompactObservationReadsTheReferencedClusterAsync(string type, string body)
    {
        EditorTestClock clock = new();
        byte[] data = Encoding.UTF8.GetBytes(body);
        NodePreviewSnapshot snapshot = new(Guid.NewGuid(), "source", "saved", "node", "source-peer", clock.GetUtcNow(), "text/plain", data.Length);
        int reads = 0;
        using ClientHttpHandler handler = new(async (message, token) =>
        {
            Assert.Equal("source-cluster", message.Headers.GetValues("X-Contents-Cluster").Single());
            Assert.Equal("source-peer", message.Headers.GetValues("X-Contents-Peer").Single());
            NodePreviewChunkRequest request = JsonSerializer.Deserialize<NodePreviewChunkRequest>(await message.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options)!;
            Assert.Equal(snapshot.Id, request.Id);
            reads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new NodePreviewChunk(snapshot.Id, 0, data), ContractJson.Options), Encoding.UTF8, "application/json") };
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://127.0.0.1") };
        ManagementApi api = new(client, new(clock)) { SelectedClusterId = "source-cluster", SelectedPeerId = "source-peer" };
        BrowserTestRuntime browser = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(browser);
        services.AddSingleton(new UiText(new(browser)));
        services.AddSingleton(new EditorDocument(new(browser)));
        services.AddSingleton(new EditorPreviews(api));
        services.AddSingleton(new EditorConcurrencyQueues(api));
        services.AddSingleton<EditorPresentationCache>();
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<WfDashboardCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Reference"] = new WfDashboardReference("source", "node", type), ["Cluster"] = "source-cluster", ["Snapshots"] = new[] { snapshot }, ["Available"] = true,
        })).ConfigureAwait(true)).ToHtmlString()).ConfigureAwait(true);
        Assert.Equal(1, reads);
        Assert.Contains("12", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Progress requires", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Captured revision", html, StringComparison.Ordinal);
    }

    /// <summary>表示順の検証に必要な軽量一覧項目です。</summary>
    /// <returns>未展開の候補です。</returns>
    private static WorkflowListSummary Summary(string id) => new(id, id, "revision", "revision", DateTimeOffset.UtcNow, 0, false, id);
}
