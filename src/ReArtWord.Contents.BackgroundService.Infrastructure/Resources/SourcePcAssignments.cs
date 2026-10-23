using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources;

/// <summary>待受のPC選択を共有し、通常の負荷変動では担当を維持します。</summary>
public sealed class SourcePcAssignments
{
    /// <summary>ワークフローごとに分離する担当保存領域の接頭辞です。</summary>
    public static string ScopePrefix => "execution:source-pcs:";
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly ReplicatedEntryConfirmation confirmation;
    private readonly SourceSupervisor sources;
    private readonly NodeRegistry registry;
    private readonly Func<IReadOnlyDictionary<string, ResourceSample>> observations;
    private readonly TimeProvider clock;
    private readonly Dictionary<(string Workflow, string Node), FailedAttempt> attempts = [];

    // 抑制理由: 一回の確認結果を一括公開し、読取り中の旧辞書を変更しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "確認済み担当をVolatileで一括交換し、取得済みの旧一覧を変更しないため。")]
    private IReadOnlyDictionary<(string Workflow, string Node), SourcePcAssignment> confirmed = new Dictionary<(string Workflow, string Node), SourcePcAssignment>();

    /// <summary>通常の共有保存と、反映済みワークフローの参照元を接続します。</summary>
    public SourcePcAssignments(PeerCluster cluster, IDurableRepository repository, SourceSupervisor sources, NodeRegistry registry,
        Func<IReadOnlyDictionary<string, ResourceSample>> observations, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(observations);
        this.cluster = cluster;
        this.repository = repository;
        confirmation = new(cluster, repository);
        this.sources = sources;
        this.registry = registry;
        this.observations = observations;
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>現在の設定に対応する確認済み担当だけを返します。</summary>
    /// <returns>担当PC。設定変更後の未確定担当はnull。</returns>
    public TargetResolution? Read(string workflowId, string nodeId)
    {
        CompiledWorkflow? plan = sources.GetEffective(workflowId);
        SourcePcAssignment? assignment = Volatile.Read(ref confirmed).GetValueOrDefault((workflowId, nodeId));
        if (assignment is null || plan?.FindNode(nodeId) is not { } node || !string.Equals(assignment.ChoiceRevision, ChoiceRevision(plan, node), StringComparison.Ordinal))
        {
            return null;
        }
        EExecutionScope scope = node.PcExecution == EPcExecutionMode.All ? EExecutionScope.All
                                                                         : EExecutionScope.One;
        return new(scope, assignment.Targets, Selection: assignment.Decision);
    }

    /// <summary>設定・参加実体の変化に必要な担当だけを更新し、確認後に公開します。</summary>
    /// <returns>反映済みの継続入力についての担当確認。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        CompiledWorkflow[] plans = sources.GetEffectiveWorkflows().Where(plan => plan.Definition.Enabled && plan.PcSelections?.Count is > 0).ToArray();
        if (plans.Length == 0)
        {
            attempts.Clear();
            Volatile.Write(ref confirmed, new Dictionary<(string Workflow, string Node), SourcePcAssignment>());
            return;
        }
        HashSet<(string Workflow, string Node)> active = plans.SelectMany(plan => plan.PcSelections!.Keys.Select(node => (plan.Definition.Id, node))).ToHashSet();
        foreach ((string Workflow, string Node) removed in attempts.Keys.Where(key => !active.Contains(key)).ToArray())
        {
            attempts.Remove(removed);
        }
        PeerInfo[] observedPeers = cluster.GetPeers().ToArray();
        PeerInfo[] members = observedPeers.Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
        bool detached = observedPeers.Any(peer => peer.InstanceId == cluster.LocalInstanceId && peer.Status == EPeerStatus.Detached);
        bool owner = detached || string.Equals(cluster.CoordinatorId, cluster.LocalPeerId, StringComparison.Ordinal);
        string? sharedVersion = null;
        if (repository is ISharedChangeStamp changed)
        {
            sharedVersion = await changed.ReadSharedChangeStampAsync(cancellationToken).ConfigureAwait(false);
        }
        string membershipContext = ContractJson.Hash(new { owner, detached, Peers = observedPeers.OrderBy(peer => peer.PeerId, StringComparer.Ordinal)
            .Select(peer => new { peer.PeerId, peer.HostId, peer.InstanceId, peer.Status, peer.Priority, peer.Address }).ToArray() });
        Dictionary<(string Workflow, string Node), SourcePcAssignment> next = [];
        IReadOnlyDictionary<string, ResourceSample> samples = observations();
        foreach (CompiledWorkflow plan in plans)
        {
            string scope = ScopePrefix + plan.Definition.Id;
            if (detached)
            {
                scope = "peer:" + cluster.LocalPeerId + ":" + scope;
            }
            foreach (KeyValuePair<string, IReadOnlyList<PcSelection>> choice in plan.PcSelections!)
            {
                if (plan.FindNode(choice.Key) is not { } node || registry.Find(node.Type) is not ISourceNode)
                {
                    continue;
                }
                try
                {
                    SourcePcAssignment? assignment = await ResolveAsync(plan, node, scope, members, samples, owner, detached, sharedVersion, membershipContext, cancellationToken).ConfigureAwait(false);
                    if (assignment is not null)
                    {
                        attempts.Remove((plan.Definition.Id, node.Id));
                        next[(plan.Definition.Id, node.Id)] = assignment;
                    }
                }
                catch (Exception error) when (error is IOException or System.Data.Common.DbException or JsonException or InvalidOperationException or ArgumentException)
                {
                    // この待受だけを未確認に戻し、他ワークフローの担当確認は継続します。
                }
            }
        }
        Volatile.Write(ref confirmed, next);
    }

    /// <summary>既存担当を照合し、必要な場合だけCoordinatorが新しい担当を保存します。</summary>
    /// <returns>保存確認済みの担当。確認不能の場合はnull。</returns>
    private async Task<SourcePcAssignment?> ResolveAsync(CompiledWorkflow plan, Contracts.Workflow.NodeDefinition node, string scope, PeerInfo[] members,
        IReadOnlyDictionary<string, ResourceSample> samples, bool owner, bool detached, string? sharedVersion, string membershipContext, CancellationToken cancellationToken = default)
    {
        string revision = ChoiceRevision(plan, node);
        string readContext = ContractJson.Hash(new { revision, scope, membershipContext, node.PcExecution });
        (string Workflow, string Node) attemptKey = (plan.Definition.Id, node.Id);
        FailedAttempt? previous = attempts.GetValueOrDefault(attemptKey);
        IReadOnlyList<PcSelection> choices = plan.PcSelections![node.Id];
        if (sharedVersion is not null && previous is not null && clock.GetUtcNow() < previous.RetryAt && !choices.Any(UsesResource)
            && string.Equals(previous.ReadVersion, sharedVersion, StringComparison.Ordinal) && string.Equals(previous.ReadContext, readContext, StringComparison.Ordinal))
        {
            // 保存だけでなく、待機中の同じ担当本文・競合集合・確定履歴の読取りも開始しません。
            return null;
        }
        (DurableEntry? entry, IReadOnlyList<DurableEntry> conflicts) = await repository.ReadWithConflictsAsync(scope, node.Id, cancellationToken).ConfigureAwait(false);
        DurableEntry[] candidates = conflicts.Where(candidate => string.Equals(candidate.Key, node.Id, StringComparison.Ordinal)).ToArray();
        if (candidates.Length != 0 && (!owner || candidates.Any(candidate => candidate.Value?.Json.Deserialize<SourcePcAssignment>(ContractJson.Options) is not { } assignment
            || !string.Equals(assignment.WorkflowId, plan.Definition.Id, StringComparison.Ordinal) || !string.Equals(assignment.NodeId, node.Id, StringComparison.Ordinal))))
        {
            Defer(attemptKey, "conflict:" + revision, sharedVersion, readContext);
            return null;
        }
        SourcePcAssignment? saved = entry?.Value?.Json.Deserialize<SourcePcAssignment>(ContractJson.Options);
        bool same = candidates.Length == 0 && saved is not null && string.Equals(saved.ChoiceRevision, revision, StringComparison.Ordinal) && Valid(saved, plan.Definition.Id, node.Id);
        if (same && !await confirmation.CheckAsync(entry!, saved!.OperationId, saved.ConfirmedPeers, detached, cancellationToken).ConfigureAwait(false))
        {
            Defer(attemptKey, "confirm:" + saved.OperationId.ToString("N"), sharedVersion, readContext);
            return null;
        }
        bool usable = same && saved!.Targets.All(target => PeerStatusRules.CanParticipate(target.Status)
            || (detached && target.InstanceId == cluster.LocalInstanceId));
        if (!owner)
        {
            if (!usable)
            {
                Defer(attemptKey, "waiting:" + revision, sharedVersion, readContext);
            }
            return usable ? saved
                          : null;
        }
        PeerInfo[] peers = cluster.GetPeers().ToArray();
        if (usable && choices.Any(UsesResource) && saved!.Targets.All(target => peers.Any(peer => peer.InstanceId == target.InstanceId
            && (PeerStatusRules.CanParticipate(peer.Status) || (detached && peer.InstanceId == cluster.LocalInstanceId)))))
        {
            return saved;
        }
        TargetResolution resolved = new PcSelectionResolver(peers, samples, clock.GetUtcNow(), cluster.LocalInstanceId).Resolve(choices, node.PcExecution);
        PeerInfo[] available = resolved.Peers.Where(peer => PeerStatusRules.CanParticipate(peer.Status)
            || (detached && peer.InstanceId == cluster.LocalInstanceId)).ToArray();
        if (resolved.Error is not null || available.Length == 0)
        {
            // 停止・再参加中の観測は担当変更として保存せず、利用可能な実体を確認してから共有します。
            Defer(attemptKey, "unavailable:" + revision, sharedVersion, readContext);
            return null;
        }
        if (usable && saved!.Targets.Select(peer => (peer.PeerId, peer.InstanceId)).ToHashSet().SetEquals(available.Select(peer => (peer.PeerId, peer.InstanceId))))
        {
            return saved;
        }
        IReadOnlyList<string>? resolveVersions = null;
        if (candidates.Length != 0)
        {
            // 担当は現在設定から導く内部記録です。古い実体のどちらかを選ばず、固定した全候補に対して再選択を保存します。
            resolveVersions = candidates.Select(item => item.Version).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        string attemptIdentity = ContractJson.Hash(new { revision, Version = entry?.Version, resolveVersions,
            Targets = available.Select(peer => new { peer.PeerId, peer.InstanceId }).ToArray(), Members = members.OrderBy(peer => peer.PeerId, StringComparer.Ordinal).Select(peer => new { peer.PeerId, peer.InstanceId }).ToArray() });
        if (previous is not null && string.Equals(previous.Identity, attemptIdentity, StringComparison.Ordinal) && clock.GetUtcNow() < previous.RetryAt)
        {
            attempts[attemptKey] = previous with { ReadVersion = sharedVersion, ReadContext = readContext };
            return null;
        }
        SourcePcAssignment candidate = new(Guid.NewGuid(), plan.Definition.Id, node.Id, revision, available, resolved.Selection!, members);
        DurableMutation mutation = new(scope, node.Id, entry?.Version, NodeValue.FromObject(candidate), resolveVersions);
        DurableReceipt receipt;
        if (detached)
        {
            receipt = await repository.ApplyBatchAsync(candidate.OperationId, [mutation], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            receipt = await cluster.ReplicateFixedAsync(candidate.OperationId, [mutation], members, cancellationToken).ConfigureAwait(false);
        }
        if (receipt.Outcome != EOutcome.Succeeded)
        {
            Defer(attemptKey, attemptIdentity, sharedVersion, readContext);
        }
        return receipt.Outcome == EOutcome.Succeeded ? candidate
                                                     : null;
    }

    /// <summary>同じ条件だけを有限時間待ち、設定・共有状態・参加実体の変化では読取りから再確認します。</summary>
    private void Defer((string Workflow, string Node) key, string identity, string? readVersion, string readContext)
    {
        FailedAttempt? previous = attempts.GetValueOrDefault(key);
        int delay = 1;
        if (previous is not null && string.Equals(previous.Identity, identity, StringComparison.Ordinal))
        {
            delay = Math.Min(5, previous.DelaySeconds * 2);
        }
        attempts[key] = new(identity, delay, clock.GetUtcNow().AddSeconds(delay), readVersion, readContext);
    }

    /// <summary>表示位置と安全停止方針の編集では待受の担当を作り直しません。</summary>
    /// <returns>待受設定と選択条件の内容識別子。</returns>
    private static string ChoiceRevision(CompiledWorkflow plan, Contracts.Workflow.NodeDefinition node)
        => plan.PcSelectionRevisions?.GetValueOrDefault(node.Id) ?? string.Empty;

    /// <summary>入れ子の比較対象も含め、観測値で担当を選んだ条件か確認します。</summary>
    /// <returns>リソース条件が含まれる場合はtrue。</returns>
    private static bool UsesResource(PcSelection selection) => selection.Lowest is not null || selection.Within?.Any(UsesResource) == true;

    /// <summary>保存対象と実体のない担当を通常の選択として扱いません。</summary>
    /// <returns>対象に対応する担当記録か。</returns>
    public static bool Valid(SourcePcAssignment assignment, string workflowId, string nodeId)
        => assignment is not null && assignment.OperationId != Guid.Empty && string.Equals(assignment.WorkflowId, workflowId, StringComparison.Ordinal)
            && string.Equals(assignment.NodeId, nodeId, StringComparison.Ordinal) && assignment.ChoiceRevision.Length == 64
            && assignment.Targets.Count is > 0 and <= 128 && assignment.Targets.All(peer => peer.InstanceId != Guid.Empty && !string.IsNullOrWhiteSpace(peer.PeerId) && !string.IsNullOrWhiteSpace(peer.HostId))
            && assignment.Targets.Select(peer => peer.HostId).Distinct(StringComparer.Ordinal).Count() == assignment.Targets.Count;

    /// <summary>同じ確認条件で失敗を量産せず、設定・参加実体の変化では直ちに再確認する期限です。</summary>
    private sealed record FailedAttempt(string Identity, int DelaySeconds, DateTimeOffset RetryAt, string? ReadVersion, string ReadContext);
}
