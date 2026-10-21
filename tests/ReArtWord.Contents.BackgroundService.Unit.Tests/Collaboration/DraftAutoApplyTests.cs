using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;

/// <summary>共有草稿の自動反映が既定で停止し、明示設定後も通常保存の境界を守ることを確認します。</summary>
public sealed class DraftAutoApplyTests
{
    /// <summary>通常の草稿共有間隔で編集が続く間は、履歴とスナップショットを増やしません。</summary>
    /// <returns>連続した変更が一回の保存へまとまることです。</returns>
    [Fact]
    public async Task ContinuousSharedEditsAreBatchedWithoutIntermediateSnapshots()
    {
        DraftAutoApplyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync();
        fixture.Policy.Enabled = true;
        int snapshots = (await fixture.Repository.ListAsync("snapshots")).Count;
        for (int edit = 0; edit < 20; edit++)
        {
            await fixture.EditAsync("editing " + edit);
            await fixture.Apply.TickAsync();
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(1500));
            await fixture.Apply.TickAsync();
        }
        Assert.Equal(1, fixture.Activations);
        Assert.Equal(snapshots, (await fixture.Repository.ListAsync("snapshots")).Count);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Apply.TickAsync();
        Assert.Equal(2, fixture.Activations);
        Assert.Equal("editing 19", (await fixture.Store.GetAsync("flow"))!.Definition.Name);
    }

    /// <summary>保存前共有だけでは実有効版を変えず、許可後の落ち着いた候補を一回だけ保存します。</summary>
    /// <returns>有効化、待機時間と非再送の検証です。</returns>
    [Fact]
    public async Task DefaultIsExplicitAndEnabledPolicyAppliesSettledDraftOnce()
    {
        DraftAutoApplyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync();
        DraftView draft = await fixture.EditAsync("draft");
        await fixture.Apply.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await fixture.Apply.TickAsync();
        Assert.Equal("original", (await fixture.Store.GetAsync(draft.WorkflowId))!.Definition.Name);
        fixture.Policy.Enabled = true;
        await fixture.Apply.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(500));
        await fixture.Apply.TickAsync();
        Assert.Equal(1, fixture.Activations);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1600));
        await fixture.Apply.TickAsync();
        Assert.Equal("draft", (await fixture.Store.GetAsync(draft.WorkflowId))!.Definition.Name);
        Assert.Equal(2, fixture.Activations);
        int snapshots = (await fixture.Repository.ListAsync("snapshots")).Count;
        for (int count = 0; count < 5; count++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            await fixture.Apply.TickAsync();
        }
        Assert.Equal(snapshots, (await fixture.Repository.ListAsync("snapshots")).Count);
        Assert.Equal(2, fixture.Activations);
    }

    /// <summary>真の競合と不正なノード定義を、共有しているだけで実運用へ流しません。</summary>
    /// <returns>保存と実反映が維持されることです。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictedOrInvalidDraftDoesNotReplaceTheSavedWorkflow(bool invalid)
    {
        DraftAutoApplyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync();
        fixture.Policy.Enabled = true;
        DraftView original = await fixture.Collaboration.GetDraftAsync("flow") ?? throw new InvalidOperationException();
        WorkflowDefinition edited = original.Definition with { Name = "mine" };
        if (invalid)
        {
            edited = edited with { Nodes = [edited.Nodes[0] with { Type = "missing.type" }] };
        }
        await fixture.Collaboration.ApplyChangesAsync(DraftAutoApplyFixture.Change(original, edited));
        if (!invalid)
        {
            await fixture.Collaboration.ApplyChangesAsync(DraftAutoApplyFixture.Change(original, original.Definition with { Name = "other" }));
        }
        await fixture.Apply.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Apply.TickAsync();
        Assert.Equal("original", (await fixture.Store.GetAsync("flow"))!.Definition.Name);
        Assert.Equal(1, fixture.Activations);
        if (invalid)
        {
            WorkflowSaveResult? result = await fixture.Apply.ReadResultAsync("flow");
            Assert.NotNull(result);
            Assert.NotEmpty(result.Validation);
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            await fixture.Apply.TickAsync();
            Assert.Equal(result.OperationId, (await fixture.Apply.ReadResultAsync("flow"))!.OperationId);
        }
    }

    /// <summary>反映結果不明を同じ候補の新しい操作へ作り直しません。</summary>
    /// <returns>監視の再作成後も反映回数が増えないことです。</returns>
    [Fact]
    public async Task UnknownActivationIsNotRetriedByAnotherWorkerLifetime()
    {
        DraftAutoApplyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync();
        fixture.Policy.Enabled = true;
        fixture.UnknownActivation = true;
        await fixture.EditAsync("uncertain");
        await fixture.Apply.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Apply.TickAsync();
        WorkflowSaveResult? result = await fixture.Apply.ReadResultAsync("flow");
        Assert.Equal(EOutcome.Unknown, result!.Activation.Outcome);
        DraftAutoApply restarted = fixture.CreateApply();
        await restarted.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await restarted.TickAsync();
        Assert.Equal(2, fixture.Activations);
        Assert.Equal(EOutcome.Unknown, (await fixture.Store.GetSaveOperationAsync(result.OperationId))!.Activation.Outcome);
    }

    /// <summary>保存済みWFの最後のノード削除も、有効な変更として反映できます。</summary>
    /// <returns>空の既存WFの保存です。</returns>
    [Fact]
    public async Task RemovingTheLastNodeFromASavedWorkflowIsApplied()
    {
        DraftAutoApplyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync();
        fixture.Policy.Enabled = true;
        DraftView view = (await fixture.Collaboration.GetDraftAsync("flow"))!;
        await fixture.Collaboration.ApplyChangesAsync(DraftAutoApplyFixture.Change(view, view.Definition with { Nodes = [] }));
        await fixture.Apply.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Apply.TickAsync();
        Assert.Empty((await fixture.Store.GetAsync("flow"))!.Definition.Nodes);
    }
}
