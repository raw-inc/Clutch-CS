using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;

/// <summary>実保存と実UDPを、確定反映の観測先だけに接続します。</summary>
internal sealed class DraftAutoApplyFixture : IAsyncDisposable
{
    private readonly ManualSafetyFixture peers = new();
    private readonly Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);

    internal CollaborationClock Clock
    {
        get;
    } = new();

    internal PolicyProvider Policy
    {
        get;
    } = new();

    internal WorkflowStore Store
    {
        get; private set;
    } = null!;

    internal CollaborationService Collaboration
    {
        get; private set;
    } = null!;

    internal DraftAutoApply Apply
    {
        get; private set;
    } = null!;

    internal IDurableRepository Repository => peers.Repositories["a"];
    internal int Activations
    {
        get; private set;
    }

    internal bool UnknownActivation
    {
        get; set;
    }

    /// <summary>通常の保存済み・反映済みWFを作成します。</summary>
    /// <returns>Readyと初期保存の完了です。</returns>
    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await peers.StartAsync("a", true, cancellationToken: cancellationToken).ConfigureAwait(false);
        await ManualSafetyFixture.WaitAsync(() => peers.Peers["a"].GetPeers().Any(peer => peer.Status == EPeerStatus.Ready), cancellationToken).ConfigureAwait(false);
        Store = new(Repository, peers.Peers["a"], new WorkflowCompiler(new NodeRegistry([new PassNode()])))
        {
            GetEffective = id => effective.GetValueOrDefault(id),
            ActivateHandler = (workflow, token) =>
            {
                Activations++;
                effective[workflow.Definition.Id] = workflow;
                return Task.FromResult(new ConfigurationPhaseResult(UnknownActivation ? EOutcome.Unknown
                                                                                      : EOutcome.Succeeded));
            },
        };
        Collaboration = new(Store, peers.Peers["a"], Clock);
        Apply = CreateApply();
        WorkflowDefinition original = new("flow", "original", [new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }),
            Inputs: new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(1) })], []);
        await Store.SaveAsync(new(Guid.NewGuid(), original, null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Worker寿命だけを再作成します。</summary>
    /// <returns>同じ保存・草稿を使う反映境界です。</returns>
    internal DraftAutoApply CreateApply() => new(Store, Collaboration, peers.Peers["a"], Policy, Clock);

    /// <summary>比較元に対応する通常の草稿編集です。</summary>
    /// <returns>確認した未保存草稿です。</returns>
    internal async Task<DraftView> EditAsync(string name, CancellationToken cancellationToken = default)
    {
        DraftView view = (await Collaboration.GetDraftAsync("flow", cancellationToken).ConfigureAwait(false))!;
        return await Collaboration.ApplyChangesAsync(Change(view, view.Definition with { Name = name }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>比較元と操作IDを固定します。</summary>
    /// <returns>通常の編集要求です。</returns>
    internal static DraftChangeRequest Change(DraftView view, WorkflowDefinition definition)
        => new(Guid.NewGuid(), view.WorkflowId, Guid.NewGuid(), view.BaseRevision, view.SessionVersion, view.Definition, definition, new Dictionary<string, EEditResolution>());

    /// <summary>所有Peerを終了します。</summary>
    /// <returns>停止の完了です。</returns>
    public ValueTask DisposeAsync() => ((IAsyncDisposable)peers).DisposeAsync();

    /// <summary>共通設定の実有効値だけを切り替えます。</summary>
    internal sealed class PolicyProvider : IClusterOperationalProvider
    {
        internal bool Enabled
        {
            get; set;
        }

        /// <summary>公開済みの共有方針です。</summary>
        /// <returns>不変の設定スナップショットです。</returns>
        public ClusterOperationalSnapshot? Capture()
        {
            ClusterOperationalSettings settings = new("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep,
                new Dictionary<string, IReadOnlyList<string>>(), LiveWorkflowUpdates: Enabled);
            return new(settings, ContractJson.Hash(settings), DateTimeOffset.UtcNow);
        }
    }
}
