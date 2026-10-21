using System.Net;
using System.Net.Http.Json;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>表示するWFだけを取得し、同名IDの別クラスタや古い応答を混ぜません。</summary>
public sealed class OverviewWorkflowNamesTests
{
    /// <summary>可視IDだけをまとめて照会し、名前変更は保持期限後に反映します。</summary>
    /// <returns>要求の範囲とキャッシュの更新です。</returns>
    [Fact]
    public async Task NamesAreBoundedCachedAndScopedToClusterAndPeerAsync()
    {
        EditorTestClock clock = new();
        int requests = 0;
        string name = "設定した名前";
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            requests++;
            Assert.Equal("/api/workflows/query", request.RequestUri!.AbsolutePath);
            WorkflowListQuery query = (await request.Content!.ReadFromJsonAsync<WorkflowListQuery>(ContractJson.Options, token).ConfigureAwait(false))!;
            Assert.Equal("flow", Assert.Single(query.Ids!));
            string cluster = Assert.Single(request.Headers.GetValues("X-Contents-Cluster"));
            string peer = Assert.Single(request.Headers.GetValues("X-Contents-Peer"));
            return Response("flow", name + " / " + cluster + " / " + peer);
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost") };
        OverviewWorkflowNames names = new(new(http, new(clock)));
        await names.RefreshAsync("a", "peer", ["flow", "flow"]).ConfigureAwait(true);
        Assert.Equal("設定した名前 / a / peer", names.Name("flow"));
        name = "変更した名前";
        await names.RefreshAsync("a", "peer", ["flow"]).ConfigureAwait(true);
        Assert.Equal(1, requests);
        clock.Advance(TimeSpan.FromSeconds(31));
        await names.RefreshAsync("a", "peer", ["flow"]).ConfigureAwait(true);
        Assert.Equal("変更した名前 / a / peer", names.Name("flow"));
        await names.RefreshAsync("b", "peer", ["flow"]).ConfigureAwait(true);
        Assert.Equal("変更した名前 / b / peer", names.Name("flow"));
        await names.RefreshAsync("b", "other", ["flow"]).ConfigureAwait(true);
        Assert.Equal("変更した名前 / b / other", names.Name("flow"));
        Assert.Equal("unknown", names.Name("unknown"));
    }

    /// <summary>旧Coreや欠測でも無限に再照会せず、表示する有限範囲だけを扱います。</summary>
    /// <returns>失敗時の頻度制限と識別子fallbackです。</returns>
    [Fact]
    public async Task FailureDoesNotCauseUnboundedQueriesAsync()
    {
        int requests = 0;
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            requests++;
            WorkflowListQuery query = (await request.Content!.ReadFromJsonAsync<WorkflowListQuery>(ContractJson.Options, token).ConfigureAwait(false))!;
            Assert.Equal(32, query.Ids!.Count);
            return new(HttpStatusCode.BadRequest);
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost") };
        OverviewWorkflowNames names = new(new(http));
        string[] ids = Enumerable.Range(0, 100).Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await names.RefreshAsync("a", "peer", ids).ConfigureAwait(true);
        await names.RefreshAsync("a", "peer", ids).ConfigureAwait(true);
        Assert.Equal(1, requests);
        Assert.Equal("0", names.Name("0"));
    }

    /// <summary>別の接続先へ切り替えた後に返る古い名前を採用しません。</summary>
    /// <returns>遅延応答の破棄です。</returns>
    [Fact]
    public async Task LateResponseCannotReplaceAnotherClustersNameAsync()
    {
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            string cluster = Assert.Single(request.Headers.GetValues("X-Contents-Cluster"));
            string first = "a";
            if (string.Equals(cluster, first, StringComparison.Ordinal))
            {
                ready.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            return Response("flow", cluster);
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost") };
        OverviewWorkflowNames names = new(new(http));
        Task pending = names.RefreshAsync("a", "peer", ["flow"]);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        await names.RefreshAsync("b", "peer", ["flow"]).ConfigureAwait(true);
        release.TrySetResult();
        await pending.ConfigureAwait(true);
        Assert.Equal("b", names.Name("flow"));
    }

    /// <summary>WF本文を含まない管理API応答です。</summary>
    /// <returns>一件の表示名を含む一覧です。</returns>
    private static HttpResponseMessage Response(string id, string name)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(new WorkflowListPage([new(id, name, "r", "r", DateTimeOffset.UtcNow, 1, true, "hash")], null), options: ContractJson.Options) };
}
