using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
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

/// <summary>明示離脱中の草稿確認と通知をそのPeerのUIだけへ限定します。</summary>
public sealed class DetachedCollaborationTests
{
    /// <summary>通常Peerとの往復照会も、ローカル草稿や編集者情報を交換しません。</summary>
    /// <returns>実二Peer UDPのローカル確認と隔離です。</returns>
    [Fact]
    public async Task DetachedDraftStaysLocalAndCanStillBeConfirmed()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        await fixture.StartAsync("b");
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        CollaborationService first = Service(fixture, "a");
        CollaborationService second = Service(fixture, "b");
        await fixture.Peers["a"].DetachAsync();
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["b"].GetPeers().Any(peer => peer.Status == EPeerStatus.Detached));
        WorkflowDefinition original = new("demo", "original", [], []);
        Guid ui = Guid.NewGuid();
        DraftChangeRequest change = new(Guid.NewGuid(), original.Id, ui, null, null, original, original with { Name = "local draft" }, new Dictionary<string, EEditResolution>(StringComparer.Ordinal));
        DraftView local = await first.ApplyChangesAsync(change);
        Assert.Empty(local.Validation);
        await first.UpdatePresenceAsync(new(original.Id, ui, "host-a", "editor", []));
        Assert.Null(await second.GetDraftAsync(original.Id));
        Assert.Empty(second.GetPresence(original.Id));
        Assert.Equal(local.SessionVersion, (await first.GetDraftAsync(original.Id))!.SessionVersion);
        DraftView remote = await second.ApplyChangesAsync(change with { OperationId = Guid.NewGuid(), UiInstanceId = Guid.NewGuid(), Edited = original with { Name = "remote draft" } });
        Assert.Empty(remote.Validation);
        Assert.Equal("local draft", (await first.GetDraftAsync(original.Id))!.Definition.Name);
        Assert.Equal("remote draft", (await second.GetDraftAsync(original.Id))!.Definition.Name);
    }

    /// <summary>保存と草稿を独立にした通常サービスを作ります。</summary>
    /// <returns>当該Peerの編集境界です。</returns>
    private static CollaborationService Service(ManualSafetyFixture fixture, string peerId) => new(new WorkflowStore(fixture.Repositories[peerId], fixture.Peers[peerId], new WorkflowCompiler(new NodeRegistry([]))), fixture.Peers[peerId]);
}
