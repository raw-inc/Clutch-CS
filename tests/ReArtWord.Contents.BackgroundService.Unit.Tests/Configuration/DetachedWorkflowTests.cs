using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>離脱中の設定保存と再参加後の比較・共有を実UDPで検証します。</summary>
public sealed class DetachedWorkflowTests
{
    /// <summary>未実施が確定した統合だけ、新しい明示操作で取消または再比較できます。</summary>
    /// <returns>旧操作結果固定と、保護解除後の明示操作です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedUnstartedIntegrationAllowsNewExplicitDecision(bool discard)
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await StartAsync(fixture);
        WorkflowStore store = Store(fixture, "a", new(StringComparer.Ordinal));
        WorkflowDefinition original = new("demo", "original", [], []);
        WorkflowSaveResult initial = await store.SaveAsync(new(Guid.NewGuid(), original, null));
        await fixture.Peers["a"].DetachAsync();
        WorkflowSaveResult local = await store.SaveAsync(new(Guid.NewGuid(), original with { Name = "local" }, initial.Revision));
        await fixture.Peers["a"].RejoinAsync();
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        store.IsRestoreLocked = id => true;
        DetachedWorkflowResolution request = new(Guid.NewGuid(), original.Id, local.Revision!, new Dictionary<string, EEditResolution>(StringComparer.Ordinal), ExpectedCurrentRevision: initial.Revision, CurrentRevisionConfirmed: true);
        WorkflowMergeResult rejected = await store.ResolveDetachedAsync(request);
        Assert.Equal(EOutcome.NotExecuted, rejected.Save!.Saved.Outcome);
        Assert.True(Assert.Single(await store.GetDetachedAsync()).IntegrationRetryAllowed);
        store.IsRestoreLocked = null;
        WorkflowMergeResult next = await store.ResolveDetachedAsync(request with { OperationId = Guid.NewGuid(), Discard = discard });
        Assert.Equal(EOutcome.Succeeded, next.Save!.Saved.Outcome);
        Assert.Empty(await store.GetDetachedAsync());
        Assert.Equal(ContractJson.Hash(rejected), ContractJson.Hash(await store.ResolveDetachedAsync(request)));
    }

    /// <summary>ローカル保存は即時利用しても共有正本を変えず、再参加で非競合だけを共有します。</summary>
    /// <returns>ローカル版と共有版の分離です。</returns>
    [Fact]
    public async Task LocalSaveRemainsIndependentThenMergesAfterExplicitRejoin()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await StartAsync(fixture);
        Dictionary<string, CompiledWorkflow> active = new(StringComparer.Ordinal);
        WorkflowStore first = Store(fixture, "a", active);
        WorkflowStore second = Store(fixture, "b", new(StringComparer.Ordinal));
        WorkflowDefinition definition = new("demo", "original", [], []);
        WorkflowSaveResult initial = await first.SaveAsync(new(Guid.NewGuid(), definition, null));
        await fixture.Peers["a"].DetachAsync();
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["b"].GetPeers().Any(peer => string.Equals(peer.PeerId, "a", StringComparison.Ordinal) && peer.Status == EPeerStatus.Detached));
        WorkflowSaveRequest request = new(Guid.NewGuid(), definition with { Name = "local" }, initial.Revision);
        WorkflowSaveResult local = await first.SaveAsync(request);
        Assert.Equal(EOutcome.Succeeded, local.Saved.Outcome);
        Assert.Equal(EOutcome.NotExecuted, local.Shared.Outcome);
        Assert.True(local.LocalOnly);
        Assert.True((await first.GetAsync("demo"))!.LocalOnly);
        Assert.Equal("local", active["demo"].Definition.Name);
        Assert.Equal("original", (await second.GetAsync("demo"))!.Definition.Name);
        Assert.Equal("local", Assert.Single(await first.ListAsync()).Definition.Name);
        first.CanEditDetached = () => false;
        Assert.Equal(EOutcome.NotExecuted, (await first.SaveAsync(request with { OperationId = Guid.NewGuid(), ExpectedRevision = local.Revision })).Saved.Outcome);
        first.CanEditDetached = () => true;
        await fixture.Peers["a"].RejoinAsync();
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        Assert.Single(await first.GetDetachedAsync());

        // 共有の確定応答が遅れた場合は、同じ統合操作を照会して完了を確認します。
        using CancellationTokenSource confirmation = new(TimeSpan.FromSeconds(15));
        Guid? integrationOperation = null;
        do
        {
            await first.ReconcileDetachedAsync(confirmation.Token);
            IReadOnlyList<DetachedWorkflowState> pending = await first.GetDetachedAsync(confirmation.Token);
            if (pending.Count == 0)
            {
                break;
            }
            if (Assert.Single(pending).IntegrationOperationId is Guid operation)
            {
                integrationOperation ??= operation;
                Assert.Equal(integrationOperation, operation);
            }
            await Task.Delay(25, confirmation.Token);
        }
        while (true);
        Assert.Empty(await first.GetDetachedAsync());
        Assert.Equal("local", (await second.GetAsync("demo"))!.Definition.Name);
        Assert.False((await first.GetAsync("demo"))!.LocalOnly);
        Assert.True((await first.GetSaveOperationAsync(request.OperationId))!.LocalOnly);
    }

    /// <summary>未観測の共有変更を上書きせず、確認済み比較または明示取消だけで閉じます。</summary>
    /// <returns>競合保持・比較版検証・元取消結果です。</returns>
    [Fact]
    public async Task RejoinRetainsConflictAndDiscardHasStableOriginalResult()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await StartAsync(fixture);
        WorkflowStore first = Store(fixture, "a", new(StringComparer.Ordinal));
        WorkflowStore second = Store(fixture, "b", new(StringComparer.Ordinal));
        WorkflowDefinition definition = new("demo", "original", [], []);
        WorkflowSaveResult initial = await first.SaveAsync(new(Guid.NewGuid(), definition, null));
        await fixture.Peers["a"].DetachAsync();
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["b"].GetPeers().Any(peer => string.Equals(peer.PeerId, "a", StringComparison.Ordinal) && peer.Status == EPeerStatus.Detached));
        WorkflowSaveResult local = await first.SaveAsync(new(Guid.NewGuid(), definition with { Name = "local" }, initial.Revision));
        WorkflowSaveResult remote = await second.SaveAsync(new(Guid.NewGuid(), definition with { Name = "remote" }, initial.Revision));
        Assert.Equal(EOutcome.Succeeded, remote.Shared.Outcome);
        await fixture.Peers["a"].RejoinAsync();
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        await first.ReconcileDetachedAsync();
        DetachedWorkflowState pending = Assert.Single(await first.GetDetachedAsync());
        Assert.NotEmpty(pending.Conflicts);
        DetachedWorkflowResolution stale = new(Guid.NewGuid(), "demo", local.Revision!, new Dictionary<string, EEditResolution>(StringComparer.Ordinal) { ["/name"] = EEditResolution.UseEdited }, ExpectedCurrentRevision: initial.Revision, CurrentRevisionConfirmed: true);
        Assert.NotEmpty((await first.ResolveDetachedAsync(stale)).Validation);
        Assert.Equal("remote", (await second.GetAsync("demo"))!.Definition.Name);
        DetachedWorkflowResolution discard = stale with { OperationId = Guid.NewGuid(), Discard = true };
        WorkflowMergeResult discarded = await first.ResolveDetachedAsync(discard);
        Assert.Equal(EOutcome.Succeeded, discarded.Save!.Saved.Outcome);
        Assert.True(discarded.Save.LocalOnly);
        Assert.Equal("Detached.Discarded", discarded.Save.Shared.Reason);
        Assert.Empty(await first.GetDetachedAsync());
        Assert.Equal(ContractJson.Hash(discarded), ContractJson.Hash(await first.ResolveDetachedAsync(discard)));
        Assert.Equal(ContractJson.Hash(discarded.Save), ContractJson.Hash(await first.GetSaveOperationAsync(discard.OperationId)));
    }

    /// <summary>同じ共有状態を使う二Peerを開始します。</summary>
    /// <returns>実参加確認です。</returns>
    private static async Task StartAsync(ManualSafetyFixture fixture)
    {
        await fixture.StartAsync("a", true).ConfigureAwait(false);
        await fixture.StartAsync("b").ConfigureAwait(false);
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2).ConfigureAwait(false);
    }

    /// <summary>有効版の実採用を観測できる通常storeを作ります。</summary>
    /// <returns>保存・比較・適用を行うstoreです。</returns>
    private static WorkflowStore Store(ManualSafetyFixture fixture, string id, Dictionary<string, CompiledWorkflow> effective) => new(fixture.Repositories[id], fixture.Peers[id], new WorkflowCompiler(new NodeRegistry([])))
    {
        GetEffective = workflow => effective.GetValueOrDefault(workflow),
        ActivateHandler = (workflow, token) =>
        {
            effective[workflow.Definition.Id] = workflow;
            return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
        },
    };
}
