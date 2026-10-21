using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;

/// <summary>内容分岐だけを用途別に隔離し、固定候補の明示解消を永続共有します。</summary>
public sealed class ClusterConflictService : IDisposable
{
    private static string JournalKind => "conflict.resolve";
    private readonly IDurableRepository repository;
    private readonly PeerCluster cluster;
    private readonly WorkflowStore workflows;
    private readonly ClusterConfigurationStore operational;
    private readonly NodeRegistry registry;
    private readonly WorkflowCompiler compiler;
    private readonly ConfigurationOperationJournal journal;
    private readonly SemaphoreSlim changes = new(1, 1);
    private ConflictSnapshot current = new([]);
    private bool loaded;

    /// <summary>既存の保存・意味検証・ローカル反映を共用します。</summary>
    public ClusterConflictService(IDurableRepository repository, PeerCluster cluster, WorkflowStore workflows, ClusterConfigurationStore operational, NodeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(operational);
        ArgumentNullException.ThrowIfNull(registry);
        this.repository = repository;
        this.cluster = cluster;
        this.workflows = workflows;
        this.operational = operational;
        this.registry = registry;
        compiler = new(registry);
        journal = new(repository);
        operational.ConflictProjection = ProjectOperational;
    }

    /// <summary>全候補を取得し、公開可能な内容と依存範囲だけを返します。</summary>
    /// <returns>現在の分岐一覧です。</returns>
    public Task<IReadOnlyList<StateConflict>> GetAsync(CancellationToken cancellationToken = default) => GetCoreAsync(cancellationToken);

    /// <summary>確認後の一覧を所有コピーで返します。</summary>
    /// <returns>現在の候補です。</returns>
    private async Task<IReadOnlyList<StateConflict>> GetCoreAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return ContractJson.Copy(Volatile.Read(ref current).Items);
    }

    /// <summary>通常候補になる前に隔離可能性を確認します。</summary>
    /// <returns>全分岐を既知用途へ隔離できるか。</returns>
    public async ValueTask<bool> PrepareAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return loaded && Volatile.Read(ref current).Items.All(item => item.Resolvable);
    }

    /// <summary>読取りだけで全候補を更新し、途中状態を公開しません。</summary>
    /// <returns>候補確認の完了です。</returns>
    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> entries = await repository.GetConflictsAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        List<StateConflict> conflicts = entries.GroupBy(entry => (entry.Scope, entry.Key)).Select(group => ConflictInspection.Describe(group.ToArray())).ToList();
        string activation = "cluster.operational.activation";
        DurableEntry[] operationalCandidates = entries.Where(entry => string.Equals(entry.Scope, activation, StringComparison.Ordinal)).ToArray();
        ClusterOperationalSnapshot? projection = null;
        if (operationalCandidates.Length != 0)
        {
            DurableEntry? baseline = await ConflictAncestry.FindAsync(operationalCandidates, repository, cancellationToken).ConfigureAwait(false);
            if (baseline?.Value is not null && operationalCandidates.All(entry => entry.Value is not null))
            {
                (_, NodeValue? projected) = ConflictOperationalMerge.Create(baseline, operationalCandidates);
                projection = projected?.Json.Deserialize<ClusterOperationalSnapshot>();
                if (projection is not null && ClusterConfigurationValidation.Validate(projection.Settings).Count == 0)
                {
                    for (int index = 0; index < conflicts.Count; index++)
                    {
                        if (string.Equals(conflicts[index].Scope, activation, StringComparison.Ordinal))
                        {
                            conflicts[index] = conflicts[index] with { OperationalFields = projection.UnresolvedFields ?? [] };
                        }
                    }
                }
            }
        }
        Volatile.Write(ref current, new ConflictSnapshot(conflicts, projection, operationalCandidates.Length != 0));
        Volatile.Write(ref loaded, true);
    }

    /// <summary>確認済み共通祖先の非競合編集だけを、現担当が同じ共有解消経路へ渡します。</summary>
    /// <returns>自動統合できる差分の確認完了です。</returns>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(cluster.CoordinatorId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            return;
        }
        foreach (StateConflict conflict in Volatile.Read(ref current).Items.OrderBy(item => item.Scope, StringComparer.Ordinal))
        {
            IReadOnlyList<DurableEntry> candidates = await FindCandidatesAsync(conflict.Id, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0 || !conflict.Resolvable)
            {
                continue;
            }
            (bool safe, NodeValue? value) = await ConflictAutoMerge.CreateAsync(candidates, repository, cancellationToken).ConfigureAwait(false);
            if (!safe)
            {
                continue;
            }
            string[] versions = candidates.Select(entry => entry.Version).Order(StringComparer.Ordinal).ToArray();
            string identity = ContractJson.Hash(new { conflict.Id, versions, value });
            Guid operation = new(Convert.FromHexString(identity).AsSpan(0, 16));
            DurableEntry? selected = candidates.FirstOrDefault(entry => string.Equals(ContractJson.Hash(entry.Value), ContractJson.Hash(value), StringComparison.Ordinal));
            await ResolveAsync(new(operation, conflict.Id, versions, selected?.Version, value, selected is null), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>独立ノードは許し、競合へ依存する新しい実行だけを拒否します。</summary>
    /// <returns>利用不能理由、確認済み独立ならnullです。</returns>
    public string? GetExecutionProblem(string workflowId, NodeDefinition node)
    {
        if (!Volatile.Read(ref loaded))
        {
            return "Conflict.StateUnconfirmed";
        }
        IReadOnlyList<StateConflict> conflicts = ExecutionConflicts(workflowId);
        return ConflictInspection.GetExecutionProblem(conflicts, workflowId, node, registry);
    }

    /// <summary>保存候補だけの分岐は、確認済み旧有効版の実行を停止させません。</summary>
    /// <returns>新規実行が依存する内容だけです。</returns>
    private List<StateConflict> ExecutionConflicts(string workflowId)
    {
        bool hasEffective = workflows.GetEffective?.Invoke(workflowId) is not null;
        ClusterOperationalSnapshot? active = operational.Capture();
        List<StateConflict> result = [];
        foreach (StateConflict item in Volatile.Read(ref current).Items)
        {
            bool savedDefinition = string.Equals(item.Scope, "workflows", StringComparison.Ordinal) || item.Scope.StartsWith("workflow.history:", StringComparison.Ordinal);
            bool savedCommon = item.Scope is "cluster.operational" or "cluster.operational.history";
            if ((hasEffective && savedDefinition) || (active is not null && savedCommon))
            {
                continue;
            }
            result.Add(item);
        }
        if (active?.UnresolvedFields is not null && active.UnresolvedFields.Count != 0)
        {
            result.Add(new("effective-projection", "cluster.operational.activation", ClusterConfigurationStore.Id, [], [], active.UnresolvedFields, false, true));
        }
        return result;
    }

    /// <summary>任意候補を使わず、実共通祖先から確認した非依存値だけを公開します。</summary>
    /// <returns>派生版または未確認のnullです。</returns>
    private ClusterOperationalSnapshot? ProjectOperational(ClusterOperationalSnapshot candidate)
    {
        ConflictSnapshot snapshot = Volatile.Read(ref current);
        if (snapshot.HasOperationalConflict)
        {
            return snapshot.Projection;
        }
        return candidate;
    }

    /// <summary>元操作の最終結果だけを問い合わせます。</summary>
    /// <returns>既知結果、未受理ならnullです。</returns>
    public async Task<ConflictResolveResult?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        ConfigurationOperation? operation = await journal.ReadAsync(JournalKind, operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return null;
        }
        return operation.Result?.Deserialize<ConflictResolveResult>() ?? Rejected(operationId, "Conflict.OriginalResultUnknown", EOutcome.Unknown);
    }

    /// <summary>全候補集合を原子比較し、全固定参加者の永続ACK後だけ共有済みとします。</summary>
    /// <returns>元要求に固定した解消結果です。</returns>
    public async Task<ConflictResolveResult> ResolveAsync(ConflictResolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ContractJson.Copy(request);
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConfigurationOperation? prior = await journal.ReadAsync(JournalKind, request.OperationId, cancellationToken).ConfigureAwait(false);
            if (prior is not null)
            {
                if (!string.Equals(prior.Fingerprint, ContractJson.Hash(request), StringComparison.Ordinal))
                {
                    return Rejected(request.OperationId, "Conflict.OperationMismatch");
                }
                return prior.Result?.Deserialize<ConflictResolveResult>() ?? Rejected(request.OperationId, "Conflict.OriginalResultUnknown", EOutcome.Unknown);
            }
            IReadOnlyList<DurableEntry> candidates = await FindCandidatesAsync(request.ConflictId, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0 || !candidates.Select(entry => entry.Version).ToHashSet(StringComparer.Ordinal).SetEquals(request.ExpectedVersions))
            {
                return Rejected(request.OperationId, "Conflict.CandidateSetChanged");
            }
            StateConflict conflict = ConflictInspection.Describe(candidates);
            DurableEntry? selected = candidates.FirstOrDefault(entry => string.Equals(entry.Version, request.SelectedVersion, StringComparison.Ordinal));
            bool mergeAllowed = ConflictCandidateValidation.IsWorkflow(conflict.Scope) || ConflictCandidateValidation.IsOperational(conflict.Scope) || Runtime.State.NodeStateScope.IsShared(conflict.Scope);
            if (request.OperationId == Guid.Empty || (request.UseMergedValue && (request.SelectedVersion is not null || conflict.ValuesHidden || !mergeAllowed)) || (!request.UseMergedValue && selected is null))
            {
                return Rejected(request.OperationId, "Conflict.SelectionRequired");
            }
            NodeValue? value = selected?.Value;
            if (request.UseMergedValue)
            {
                value = request.MergedValue;
            }
            IReadOnlyList<ValidationIssue> issues = ConflictCandidateValidation.Validate(conflict, value, compiler);
            if (issues.Count != 0)
            {
                return Rejected(request.OperationId, "Conflict.InvalidCandidate") with { Issues = issues };
            }
            IReadOnlyList<DurableMutation>? mutations = await ConflictResolutionChanges.CreateAsync(conflict, value, request.ExpectedVersions, repository, cluster, cancellationToken).ConfigureAwait(false);
            if (mutations is null)
            {
                return Rejected(request.OperationId, "Conflict.RestoreLocked");
            }
            if (!await journal.BeginAsync(JournalKind, request.OperationId, request, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return Rejected(request.OperationId, "Conflict.BeginUnconfirmed", EOutcome.Unknown);
            }
            PeerInfo[] members = cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
            DurableReceipt receipt = await cluster.ReplicateFixedAsync(request.OperationId, mutations, members, cancellationToken).ConfigureAwait(false);
            ConfigurationPhaseResult saved = new(receipt.Outcome, receipt.Reason);
            ConfigurationPhaseResult applied = new(EOutcome.NotExecuted);
            ConfigurationPhaseResult captured = new(EOutcome.NotExecuted);
            if (receipt.Outcome == EOutcome.Succeeded)
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                (captured, applied) = await ConflictSavedSnapshot.CompleteAsync(request.OperationId, conflict, value, cluster, workflows, operational, members, cancellationToken).ConfigureAwait(false);
                LocalPreparationResult definitions = await workflows.PrepareLocalAsync(cancellationToken).ConfigureAwait(false);
                LocalPreparationResult common = await operational.PrepareLocalAsync(cancellationToken).ConfigureAwait(false);
                bool savedScope = conflict.Scope is "workflows" or "cluster.operational";
                if (!savedScope)
                {
                    applied = new(EOutcome.Succeeded);
                }
                if (definitions.Issues.Count != 0 || common.State == ELocalPreparationState.Unavailable)
                {
                    applied = new(EOutcome.NotExecuted, "Conflict.LocalPreparationUnconfirmed");
                }
                await cluster.RefreshParticipationAsync(cancellationToken).ConfigureAwait(false);
            }
            ConflictResolveResult result = new(request.OperationId, saved, saved, applied, [], captured);
            await journal.CompleteAsync(JournalKind, request.OperationId, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            return await GetOperationAsync(request.OperationId, CancellationToken.None).ConfigureAwait(false) ?? Rejected(request.OperationId, "Conflict.ResultUnconfirmed", EOutcome.Unknown);
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>公開識別子からその時点の全候補だけを取得します。</summary>
    /// <returns>同一scope/keyの候補です。</returns>
    private async Task<IReadOnlyList<DurableEntry>> FindCandidatesAsync(string id, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> entries = await repository.GetConflictsAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        return entries.GroupBy(entry => (entry.Scope, entry.Key)).FirstOrDefault(group => string.Equals(ContractJson.Hash(new { group.Key.Scope, group.Key.Key }), id, StringComparison.Ordinal))?.ToArray() ?? [];
    }

    /// <summary>開始していない段階を成功にしません。</summary>
    /// <returns>未実行または元結果不明です。</returns>
    private static ConflictResolveResult Rejected(Guid id, string reason, EOutcome outcome = EOutcome.NotExecuted)
    {
        ConfigurationPhaseResult result = new(outcome, reason);
        return new(id, result, result, result, [], result);
    }

    /// <summary>明示解消のローカル排他を解放します。</summary>
    public void Dispose() => changes.Dispose();

    /// <summary>読取側へ途中の候補更新を見せない所有単位です。</summary>
    private sealed record ConflictSnapshot(IReadOnlyList<StateConflict> Items, ClusterOperationalSnapshot? Projection = null, bool HasOperationalConflict = false);
}
