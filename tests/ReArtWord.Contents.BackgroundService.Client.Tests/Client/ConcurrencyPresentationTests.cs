using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>待機一覧を小さい状態取得で表示し、区間選択を同じグラフから判定します。</summary>
public sealed class ConcurrencyPresentationTests
{
    /// <summary>通常図とWFDashboardで必要な列だけを表示し、不明状態を空として見せません。</summary>
    /// <returns>実Razorの一覧、時刻差と安全な文字列表示です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RendersCurrentQueueWithAppropriateColumnsAsync(bool compact)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        WorkflowConcurrencySnapshot snapshot = new("workflow", "limit", 2, 1,
            [new(Guid.NewGuid(), Guid.NewGuid(), "peer<script>", now.AddSeconds(-3), now.AddSeconds(7), false)], now);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ConcurrencyQueuePanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Snapshot"] = snapshot, ["Compact"] = compact,
        })).ConfigureAwait(true)).ToHtmlString()).ConfigureAwait(true);
        Assert.Contains("<table", html, StringComparison.Ordinal);
        Assert.Contains("peer&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("3.0s", html, StringComparison.Ordinal);
        Assert.Equal(!compact, html.Contains("7.0s", StringComparison.Ordinal));
        WfDashboardReference reference = new("workflow", "limit", ConcurrencyNodeTypes.Start);
        string offline = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<WfDashboardCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Reference"] = reference, ["Available"] = false,
            ["State"] = new WfDashboardNodeState(reference, "WF", "Limit", "revision", true) { Concurrency = snapshot },
        })).ConfigureAwait(true)).ToHtmlString()).ConfigureAwait(true);
        Assert.DoesNotContain("<table", offline, StringComparison.Ordinal);
        Assert.Contains("unconfirmed", offline, StringComparison.Ordinal);
    }

    /// <summary>全ノードを一括照会し、文書切替後に遅れて返った古い結果を採用しません。</summary>
    /// <returns>一回のHTTP、旧図の隔離、切断時の表示破棄です。</returns>
    [Fact]
    public async Task BatchesQueueReadsAndRejectsLateDocumentResponseAsync()
    {
        TaskCompletionSource response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using ClientHttpHandler handler = new(async (message, token) =>
        {
            calls++;
            WorkflowConcurrencyTarget[] targets = JsonSerializer.Deserialize<WorkflowConcurrencyTarget[]>(await message.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options)!;
            Assert.Equal(2, targets.Length);
            await response.Task.WaitAsync(token).ConfigureAwait(false);
            WorkflowConcurrencySnapshot[] values = targets.Select(target => new WorkflowConcurrencySnapshot(target.WorkflowId, target.NodeId, 1, 0, [], DateTimeOffset.UtcNow)).ToArray();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(values, ContractJson.Options), Encoding.UTF8, "application/json") };
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://127.0.0.1") };
        ManagementApi api = new(client);
        EditorConcurrencyQueues queues = new(api);
        WorkflowDefinition definition = new("workflow", "Workflow", [Node("limit", ConcurrencyNodeTypes.Start), Node("second", ConcurrencyNodeTypes.Start)], []);
        Task running = queues.RefreshAsync("cluster", "peer", definition);
        try
        {
            await queues.RefreshAsync("cluster", "peer", definition).ConfigureAwait(true);
            await queues.RefreshAsync("cluster", "peer", definition with { Id = "other" }).ConfigureAwait(true);
            response.TrySetResult();
            await running.ConfigureAwait(true);
            Assert.Equal(1, calls);
            Assert.Null(queues.Find("cluster", "workflow", "limit"));
            Assert.Null(queues.Find("cluster", "other", "limit"));
            await queues.RefreshAsync("cluster", "peer", definition with { Id = "other" }).ConfigureAwait(true);
            Assert.Equal(1, queues.Find("cluster", "other", "limit")?.ActiveCount);
            queues.Reset();
            Assert.Null(queues.Find("cluster", "other", "limit"));
        }
        finally
        {
            response.TrySetResult();
            await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>専用の区間接続をデータと区別し、SubWorkflow分離で片方だけ移しません。</summary>
    [Fact]
    public void ScopePortIsAnExecutionBoundaryDuringDisplayAndExtraction()
    {
        Contracts.Nodes.NodeDescriptor start = new(ConcurrencyNodeTypes.Start, "Limit", "Control", "", Contracts.Nodes.ENodeKind.Action, Contracts.Nodes.EExecutionLocation.Independent,
            [], [new("scope", Contracts.Nodes.EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { }));
        Contracts.Nodes.NodeDescriptor end = start with { Type = ConcurrencyNodeTypes.End, Inputs = [new("scope", Contracts.Nodes.EPortValueKind.Json)], Outputs = [] };
        Assert.Equal("port.type.concurrencyScope", GraphPresentation.Port(start, "scope", true).Label);
        Assert.Equal("port.type.concurrencyScope", GraphPresentation.Port(end, "scope", false).Label);
        WorkflowDefinition definition = new("workflow", "Workflow", [Node("limit", start.Type), Node("end", end.Type)], [new("limit", "scope", "end", "scope")]);
        UiText text = new(new(new BrowserTestRuntime()));
        Assert.Throws<InvalidOperationException>(() => new SubworkflowExtraction(definition, new HashSet<string>(["limit"], StringComparer.Ordinal), [start, end], text).Create());
        GraphExtractionPlan plan = new SubworkflowExtraction(definition, new HashSet<string>(["limit", "end"], StringComparer.Ordinal), [start, end], text).Create();
        Assert.Contains(plan.Recipe.Wires, wire => wire.FromPort is "scope" && wire.ToPort is "scope");
    }

    /// <summary>区間とカードの表示に必要な最小ノードです。</summary>
    /// <returns>入力値を保持しない定義です。</returns>
    private static NodeDefinition Node(string id, string type) => new(id, type, JsonSerializer.SerializeToElement(new { }));
}
