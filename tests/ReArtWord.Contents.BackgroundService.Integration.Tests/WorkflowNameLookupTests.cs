using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>名前の絞込みを実Core間で検証し、WF本文の取得や先頭ページへの依存を防ぎます。</summary>
public sealed class WorkflowNameLookupTests
{
    /// <summary>別Peerから必要なIDだけを軽量に取得し、絞込み後のページ境界を維持します。</summary>
    /// <returns>保存・共有・実通信での一覧読取りです。</returns>
    [Fact]
    public async Task FilteredSummariesResolveNamesOnAnotherPeerAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2
            && (await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2).ConfigureAwait(true);
        foreach (string id in new[] { "a-target", "middle", "z-target" })
        {
            WorkflowDefinition definition = new(id, "表示名 " + id, [new("value", "control.pass", JsonSerializer.SerializeToElement(new { }),
                new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(new string('x', 50000)) }, new(EExecutionScope.One, ["a"]))], []);
            WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, saved.Saved.Outcome);
        }
        await CorePeer.WaitAsync(async token => (await second.GetAsync<WorkflowListPage>("workflows.list", new WorkflowListQuery(Ids: ["a-target", "z-target"]), token).ConfigureAwait(true)).Entries.Count == 2).ConfigureAwait(true);
        WorkflowListQuery query = new(1, MaximumBytes: 16384, Ids: ["a-target", "z-target"]);
        WorkflowListPage page = await second.GetAsync<WorkflowListPage>("workflows.list", query).ConfigureAwait(true);
        Assert.Equal("表示名 a-target", Assert.Single(page.Entries).Name);
        Assert.NotNull(page.Cursor);
        WorkflowListPage next = await second.GetAsync<WorkflowListPage>("workflows.list", query with { Cursor = page.Cursor }).ConfigureAwait(true);
        Assert.Equal("表示名 z-target", Assert.Single(next.Entries).Name);
        Assert.Null(next.Cursor);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, ContractJson.Options).Length < 16384);
        Assert.Empty((await second.GetAsync<WorkflowListPage>("workflows.list", new WorkflowListQuery(Ids: ["missing"])).ConfigureAwait(true)).Entries);
        Assert.Empty((await second.GetAsync<WorkflowListPage>("workflows.list", new WorkflowListQuery(Ids: [])).ConfigureAwait(true)).Entries);
        Assert.False(JsonSerializer.SerializeToElement(new WorkflowListQuery(), ContractJson.Options).TryGetProperty("ids", out _));
    }
}
