using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Nodes.State;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.State;

/// <summary>合法ワークフロー名がPeer内部の運用状態を変更しないことを検証します。</summary>
public sealed class NodeStateScopeTests
{
    /// <summary>membershipというワークフローのknownキーも、再起動に必要な既知集合から分離します。</summary>
    /// <returns>実ノード書込とPeer再起動の検証です。</returns>
    [Fact]
    public async Task MembershipNamedWorkflowCannotOverwriteKnownPeerState()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        IDurableRepository repository = fixture.Repositories["a"];
        DurableEntry before = (await repository.ReadAsync("peer:a:membership", "known"))!;
        Assert.NotNull(before);
        NodeStateStore store = new(repository, "a");
        StateNode node = new("set", store);
        NodeExecutionContext context = new(Guid.NewGuid(), Guid.NewGuid(), "membership", "revision", "state", "a", JsonSerializer.SerializeToElement(new { scope = "peer", key = "known" }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("user value") }, TimeProvider.System);
        Assert.Equal(EOutcome.Succeeded, (await node.ExecuteAsync(context)).Outcome);
        Assert.Equal("user value", (await store.ReadAsync(NodeStateScope.Peer("a", "membership"), "known"))!.Value.GetString());
        Assert.Equal(before.Version, (await repository.ReadAsync("peer:a:membership", "known"))!.Version);
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.CompareExchangeAsync("peer:a:membership", "known", before.Version, NodeValue.Null).ConfigureAwait(true));
        await fixture.Peers["a"].StopAsync();
        await fixture.StartAsync("a");
        Assert.Equal("user value", (await store.ReadAsync(NodeStateScope.Peer("a", "membership"), "known"))!.Value.GetString());
    }

    /// <summary>他Peer・空ワークフロー・内部スコープをノード状態APIに渡せません。</summary>
    [Theory]
    [InlineData("peer:b:workflow:demo")]
    [InlineData("peer:a:effective")]
    [InlineData("peer:a:workflow:")]
    [InlineData("workflow:")]
    public void OnlyExplicitNodeScopesAreAccepted(string scope)
    {
        Assert.False(NodeStateScope.IsNodeScope(scope, "a"));
        Assert.True(NodeStateScope.TryGetWorkflowId(NodeStateScope.Peer("a", "demo"), "a", out string? id));
        Assert.Equal("demo", id);
    }
}
