using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;

/// <summary>ブラウザーの照会ごとの全文再配信を抑え、編集通知は直ちに送ります。</summary>
public sealed class DraftQueryTrafficTests
{
    /// <summary>同じ草稿の再確認をまとめても、編集と期限後の回復照会は相手へ届きます。</summary>
    /// <returns>実UDPでの照会数と編集通知です。</returns>
    [Fact]
    public async Task RepeatedReadsShareOneRecoveryQueryWithoutDelayingEdits()
    {
        ManualSafetyFixture peers = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peers.ConfigureAwait(false);
        await peers.StartAsync("a", true);
        await peers.StartAsync("b", false);
        await ManualSafetyFixture.WaitAsync(() => peers.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        int queries = 0, changes = 0;
        peers.Peers["b"].RegisterTransientHandler("collaboration.query", (_, _, _, _) =>
        {
            Interlocked.Increment(ref queries);
            return Task.CompletedTask;
        });
        peers.Peers["b"].RegisterTransientHandler("collaboration.draft", (_, _, _, _) =>
        {
            Interlocked.Increment(ref changes);
            return Task.CompletedTask;
        });
        CollaborationClock clock = new();
        WorkflowStore store = new(peers.Repositories["a"], peers.Peers["a"], new WorkflowCompiler(new NodeRegistry([])));
        CollaborationService service = new(store, peers.Peers["a"], clock);
        WorkflowDefinition original = new("flow", "original", [], []);
        DraftView view = await service.ApplyChangesAsync(new(Guid.NewGuid(), original.Id, Guid.NewGuid(), null, ContractJson.Hash(original), original, original with { Name = "draft" }, new Dictionary<string, EEditResolution>()));
        for (int index = 0; index < 8; index++)
        {
            Assert.NotNull(await service.GetDraftAsync(original.Id));
        }
        await ManualSafetyFixture.WaitAsync(() => 0 < Volatile.Read(ref queries) && 0 < Volatile.Read(ref changes));
        Assert.Equal(1, queries);
        await service.ApplyChangesAsync(new(Guid.NewGuid(), original.Id, Guid.NewGuid(), null, view.SessionVersion, view.Definition, view.Definition with { Name = "changed" }, new Dictionary<string, EEditResolution>()));
        await ManualSafetyFixture.WaitAsync(() => Volatile.Read(ref changes) == 2);
        Assert.Equal(1, queries);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("changed", (await service.GetDraftAsync(original.Id))!.Definition.Name);
        await ManualSafetyFixture.WaitAsync(() => Volatile.Read(ref queries) == 2);
    }
}
