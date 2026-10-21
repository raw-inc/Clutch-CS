using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>ローカル保存・Peer別草稿と再参加後の編集基準を検証します。</summary>
public sealed class DetachedEditorTests
{
    /// <summary>同名ワークフローでも別Peerへローカル版を混ぜず、戻ったときだけ復元します。</summary>
    /// <returns>草稿の保存・復元確認です。</returns>
    [Fact]
    public async Task LocalDraftRemainsBoundToItsPeerAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        EditorDocument model = new(storage);
        await model.SwitchTargetAsync("cluster", "a").ConfigureAwait(true);
        WorkflowDefinition definition = Definition();
        await model.LoadAsync(new(definition, ContractJson.Hash(definition), DateTimeOffset.UnixEpoch, EActivationPolicy.Auto)).ConfigureAwait(true);
        WorkflowDefinition local = definition with { Name = "local-a" };
        model.Change(_ => local);
        model.Saved(ContractJson.Hash(local), local, true);
        Assert.False(model.Draft.Dirty);
        Assert.True(model.Draft.LocalOnly);
        await model.SwitchTargetAsync("cluster", "b").ConfigureAwait(true);
        await model.LoadAsync(new(definition, ContractJson.Hash(definition), DateTimeOffset.UnixEpoch, EActivationPolicy.Auto)).ConfigureAwait(true);
        Assert.False(model.Draft.LocalOnly);
        Assert.Equal("shared", model.Draft.Document.Name);
        await model.SwitchTargetAsync("cluster", "a").ConfigureAwait(true);
        Assert.True(model.Draft.LocalOnly);
        Assert.Equal("local-a", model.Draft.Document.Name);
    }

    /// <summary>ローカル保存後の端末追加編集を共有基準へ重ね、別のローカル版結果は適用しません。</summary>
    /// <returns>統合後の編集状態です。</returns>
    [Fact]
    public async Task ReintegrationPreservesLaterDraftAndChecksLocalRevisionAsync()
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WorkflowDefinition local = Definition() with { Name = "local" };
        string revision = ContractJson.Hash(local);
        await model.LoadAsync(new(local, revision, DateTimeOffset.UnixEpoch, EActivationPolicy.Auto, true)).ConfigureAwait(true);
        model.Change(document => document with { Trace = true });
        model.SetInput("n/settings/value", "{");
        WorkflowDefinition integrated = local with { Enabled = true };
        StoredWorkflow shared = new(integrated, ContractJson.Hash(integrated), DateTimeOffset.UnixEpoch, EActivationPolicy.Auto);
        model.Reintegrated(shared, "other-revision");
        Assert.True(model.Draft.LocalOnly);
        model.Reintegrated(shared, revision);
        Assert.False(model.Draft.LocalOnly);
        Assert.True(model.Draft.Dirty);
        Assert.True(model.Draft.Document.Trace);
        Assert.True(model.Draft.Document.Enabled);
        Assert.Equal(shared.Revision, model.Draft.BaseRevision);
        Assert.Equal("{", model.ReadInput("n/settings/value"));
    }

    /// <summary>完全保存版のオフラインキャッシュもローカル未共有フラグと接続先を保持します。</summary>
    /// <returns>キャッシュ読取りの確認です。</returns>
    [Fact]
    public async Task OfflineCacheRetainsLocalOnlyWithoutCrossPeerFallbackAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        WorkflowDefinition definition = Definition();
        StoredWorkflow local = new(definition, ContractJson.Hash(definition), DateTimeOffset.UnixEpoch, EActivationPolicy.Auto, true);
        Assert.True(await EditorSavedCache.WriteAsync(storage, "cluster", "a", local).ConfigureAwait(true));
        Assert.True((await EditorSavedCache.ReadAsync(storage, "cluster", "a", definition.Id).ConfigureAwait(true))!.LocalOnly);
        Assert.Null(await EditorSavedCache.ReadAsync(storage, "cluster", "b", definition.Id).ConfigureAwait(true));
    }

    /// <summary>比較した共有版を要求へ固定し、元統合の別操作による置換を拒否します。</summary>
    [Fact]
    public void ResolutionFixesLocalAndSharedRevisions()
    {
        WorkflowDefinition baseline = Definition();
        WorkflowDefinition local = baseline with { Name = "local" };
        WorkflowDefinition current = baseline with { Name = "current" };
        StoredWorkflow original = new(baseline, ContractJson.Hash(baseline), DateTimeOffset.UnixEpoch, EActivationPolicy.Auto);
        StoredWorkflow localSaved = new(local, ContractJson.Hash(local), DateTimeOffset.UnixEpoch, EActivationPolicy.Auto, true);
        StoredWorkflow currentSaved = new(current, ContractJson.Hash(current), DateTimeOffset.UnixEpoch, EActivationPolicy.Auto);
        WorkflowMergeResult merge = DetachedWorkflowComparison.Merge(original, currentSaved, localSaved, Guid.Empty, new Dictionary<string, EEditResolution>());
        DetachedWorkflowState state = new("flow", original, localSaved, currentSaved, merge.Conflicts);
        Dictionary<string, EEditResolution> choices = merge.Conflicts.ToDictionary(item => item.Path, _ => EEditResolution.UseEdited, StringComparer.Ordinal);
        DetachedWorkflowResolution resolution = DetachedWorkflowPanel.CreateResolution(state, choices, false, EActivationPolicy.Auto);
        Assert.Equal(localSaved.Revision, resolution.ExpectedLocalRevision);
        Assert.Equal(currentSaved.Revision, resolution.ExpectedCurrentRevision);
        Assert.True(resolution.CurrentRevisionConfirmed);
        Assert.Throws<InvalidOperationException>(() => DetachedWorkflowPanel.CreateResolution(state with { IntegrationOperationId = Guid.NewGuid() }, choices, false, EActivationPolicy.Auto));
        DetachedWorkflowResolution discard = DetachedWorkflowPanel.CreateResolution(state, new Dictionary<string, EEditResolution>(), true, EActivationPolicy.Auto);
        Assert.True(discard.Discard);
        DetachedWorkflowResolution retry = DetachedWorkflowPanel.CreateResolution(state with { IntegrationOperationId = Guid.NewGuid(), IntegrationRetryAllowed = true }, choices, false, EActivationPolicy.Auto);
        Assert.NotEqual(state.IntegrationOperationId, retry.OperationId);
    }

    /// <summary>reload時に表示していないPeerのdirty草稿も画面所有者へ引き継ぎます。</summary>
    /// <returns>複数Peerの端末草稿復元です。</returns>
    [Fact]
    public async Task ReloadPreservesInactivePeerDraftAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        EditorDocument model = new(storage);
        WorkflowDefinition definition = Definition();
        StoredWorkflow local = new(definition, ContractJson.Hash(definition), DateTimeOffset.UnixEpoch, EActivationPolicy.Auto, true);
        await model.SwitchTargetAsync("cluster", "b").ConfigureAwait(true);
        await model.LoadAsync(local).ConfigureAwait(true);
        model.Change(document => document with { Name = "unsaved-b" });
        await model.SwitchTargetAsync("cluster", "a").ConfigureAwait(true);
        await model.LoadAsync(local).ConfigureAwait(true);
        await storage.WriteAsync("contents.client.cluster", "cluster", true).ConfigureAwait(true);
        await storage.WriteAsync("contents.client.peer", "a", true).ConfigureAwait(true);
        EditorDocument reloaded = new(storage);
        await reloaded.InitializeAsync().ConfigureAwait(true);
        await reloaded.SwitchTargetAsync("cluster", "b").ConfigureAwait(true);
        Assert.Equal("unsaved-b", reloaded.Draft.Document.Name);
        Assert.True(reloaded.Draft.Dirty);
        Assert.True(reloaded.Draft.LocalOnly);
    }

    /// <summary>共通の最小定義です。</summary>
    /// <returns>所有権が独立した定義です。</returns>
    private static WorkflowDefinition Definition() => new("flow", "shared", [new("n", "control.pass", JsonSerializer.SerializeToElement(new { }))], []);
}
