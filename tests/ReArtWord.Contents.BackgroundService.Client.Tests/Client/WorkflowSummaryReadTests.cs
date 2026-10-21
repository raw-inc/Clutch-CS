using System.Net;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>保存前の軽量照会が、接続先と対象の現在版を取り違えないことを確認します。</summary>
public sealed class WorkflowSummaryReadTests
{
    /// <summary>本文を要求せず、明示したPeerから一件の最新保存版だけを取得します。</summary>
    /// <returns>要求の絞込みと、反復照会で変わる保存版です。</returns>
    [Fact]
    public async Task ReadsCurrentRevisionFromOnlyTheSelectedWorkflowAsync()
    {
        int count = 0;
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/workflows/query", request.RequestUri!.AbsolutePath);
            Assert.Equal("cluster", request.Headers.GetValues("X-Contents-Cluster").Single());
            Assert.Equal("peer", request.Headers.GetValues("X-Contents-Peer").Single());
            WorkflowListQuery query = JsonSerializer.Deserialize<WorkflowListQuery>(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options)!;
            Assert.Equal("flow/?", Assert.Single(query.Ids!));
            count++;
            WorkflowListSummary item = new("flow/?", "selected", "revision-" + count, "active", DateTimeOffset.UtcNow, 1000, true, "id");
            return Reply(new([item], null));
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(client) { SelectedClusterId = "cluster", SelectedPeerId = "peer" };
        Assert.Equal("revision-1", (await WorkflowReader.ReadSummaryAsync(api, "flow/?").ConfigureAwait(true)).SavedRevision);
        Assert.Equal("revision-2", (await WorkflowReader.ReadSummaryAsync(api, "flow/?").ConfigureAwait(true)).SavedRevision);
        Assert.Equal(2, count);
    }

    /// <summary>別定義・欠落・曖昧な複数件・未完了ページを保存前の確認として使用しません。</summary>
    /// <returns>一件に確定できない応答の拒否です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RejectsMissingOrMismatchedSummaryAsync(int mode)
    {
        WorkflowListSummary item = new("flow", "selected", "revision", "active", DateTimeOffset.UtcNow, 1, false, "id");
        WorkflowListPage page = mode switch
        {
            0 => new([], null),
            1 => new([item with { Id = "other" }], null),
            2 => new([item, item], null),
            _ => new([item], "next"),
        };
        using ClientHttpHandler handler = new((_, _) => Task.FromResult(Reply(page)));
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowReader.ReadSummaryAsync(new(client), "flow")).ConfigureAwait(true);
    }

    /// <summary>既存の要約APIと同じ契約で応答します。</summary>
    /// <returns>呼出元が解放するHTTP応答です。</returns>
    private static HttpResponseMessage Reply(WorkflowListPage page) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(page, ContractJson.Options), Encoding.UTF8, "application/json"),
    };
}
