using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>宣言した一つの状態スコープだけを保存・復元・比較します。</summary>
public sealed class NodeStateSnapshotParticipant : IRuntimeSnapshotParticipant
{
    private readonly string scope;
    private readonly IDurableRepository repository;
    private readonly PeerCluster cluster;
    private readonly Func<WorkflowDefinition?>? currentWorkflow;
    private readonly Func<WorkflowDefinition, IReadOnlyList<NodeDefinition>?>? resolveNodes;

    /// <summary>対象範囲を明示し、別Peerのローカル状態を指定させません。</summary>
    public NodeStateSnapshotParticipant(string id, string scope, IDurableRepository repository, PeerCluster cluster, Func<WorkflowDefinition?>? currentWorkflow = null, Func<WorkflowDefinition, IReadOnlyList<NodeDefinition>?>? resolveNodes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(cluster);
        if (!NodeStateScope.TryGetWorkflowId(scope, cluster.LocalPeerId, out string? workflowId))
        {
            throw new ArgumentException("WorkflowまたはこのPeerの状態範囲が必要です。", nameof(scope));
        }
        Id = id;
        this.scope = scope;
        this.repository = repository;
        this.cluster = cluster;
        this.currentWorkflow = currentWorkflow;
        this.resolveNodes = resolveNodes;
        WorkflowId = workflowId!;
    }

    /// <summary>宣言した状態の識別子です。</summary>
    public string Id
    {
        get;
    }

    /// <summary>選択した設定、または現在の有効設定と状態契約を照合します。</summary>
    /// <returns>互換性を確認できるか。</returns>
    internal bool IsCompatible(NodeValue snapshot, WorkflowDefinition? candidate, bool explicitlySelected)
    {
        WorkflowDefinition? definition = candidate;
        if (!explicitlySelected)
        {
            definition = currentWorkflow?.Invoke();
        }
        definition = Resolve(definition);
        NodeStateSnapshot? state = snapshot.Json.Deserialize<NodeStateSnapshot>(ContractJson.Options);
        return definition is not null && state is not null && string.Equals(state.WorkflowId, WorkflowId, StringComparison.Ordinal) && string.Equals(state.WorkflowId, definition.Id, StringComparison.Ordinal)
            && string.Equals(state.Contract, NodeStateContract.Fingerprint(definition), StringComparison.Ordinal);
    }

    /// <summary>対応する有効ワークフローの識別子です。</summary>
    internal string WorkflowId
    {
        get;
    }

    /// <summary>削除履歴と実値を混同せず現在値だけを取得します。</summary>
    /// <returns>保存可能な状態。</returns>
    public async Task<NodeValue> CaptureAsync(CancellationToken cancellationToken = default)
    {
        WorkflowDefinition definition = Resolve(currentWorkflow?.Invoke()) ?? throw new InvalidDataException("状態に対応する有効Workflowを確認できません。");
        if (!string.Equals(definition.Id, WorkflowId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("状態の所有Workflowと有効設定が一致しません。");
        }
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync(scope, cancellationToken).ConfigureAwait(false);
        return NodeValue.FromObject(new NodeStateSnapshot(definition.Id, NodeStateContract.Fingerprint(definition), entries.Where(entry => entry.Value is not null).ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.Ordinal)));
    }

    /// <summary>状態形式を変更前に検証します。</summary>
    /// <returns>形式が不正な場合の指摘。</returns>
    public Task<IReadOnlyList<ValidationIssue>> ValidateAsync(NodeValue snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ValidationIssue> result = [];
        try
        {
            NodeStateSnapshot? state = snapshot.Json.Deserialize<NodeStateSnapshot>(ContractJson.Options);
            if (state is null || string.IsNullOrWhiteSpace(state.WorkflowId) || string.IsNullOrWhiteSpace(state.Contract) || state.Values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            {
                result = [new ValidationIssue("snapshot.stateFormat", "状態名と値が必要です。")];
            }
        }
        catch (JsonException)
        {
            result = [new ValidationIssue("snapshot.stateFormat", "状態の形式が不正です。")];
        }
        return Task.FromResult(result);
    }

    /// <summary>現在版のCASを使い、準備後の競合を上書きしません。</summary>
    /// <returns>保存結果。</returns>
    public async Task<ConfigurationPhaseResult> RestoreAsync(NodeValue snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IReadOnlyList<ValidationIssue> issues = await ValidateAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (issues.Count != 0)
        {
            return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Snapshot.InvalidState");
        }
        IReadOnlyDictionary<string, NodeValue> desired = snapshot.Json.Deserialize<NodeStateSnapshot>(ContractJson.Options)!.Values;
        IReadOnlyList<DurableEntry> current = await repository.ListAsync(scope, cancellationToken).ConfigureAwait(false);
        List<DurableMutation> mutations = [];
        foreach (string key in current.Select(entry => entry.Key).Concat(desired.Keys).Distinct(StringComparer.Ordinal))
        {
            DurableEntry? old = current.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));
            NodeValue? value = desired.GetValueOrDefault(key);
            if (!string.Equals(ContractJson.Hash(old?.Value), ContractJson.Hash(value), StringComparison.Ordinal))
            {
                mutations.Add(new DurableMutation(scope, key, old?.Version, value));
            }
        }
        if (mutations.Count == 0)
        {
            return new ConfigurationPhaseResult(EOutcome.Succeeded);
        }
        DurableReceipt receipt;
        string sharedPrefix = "workflow:";
        if (scope.StartsWith(sharedPrefix, StringComparison.Ordinal))
        {
            receipt = await cluster.ReplicateAsync(Guid.CreateVersion7(), mutations, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), mutations, cancellationToken).ConfigureAwait(false);
        }
        return new ConfigurationPhaseResult(receipt.Outcome, receipt.Reason);
    }

    /// <summary>復元対象の現在実値と比較します。</summary>
    /// <returns>実値一致。</returns>
    public async Task<bool> MatchesAsync(NodeValue snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        NodeValue actual = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(ContractJson.Hash(actual), ContractJson.Hash(snapshot), StringComparison.Ordinal);
    }

    /// <summary>保存形式を変更せず、状態契約の比較だけ実効設定へ解決します。</summary>
    /// <returns>比較用のノード一覧、解決不能ならnullです。</returns>
    private WorkflowDefinition? Resolve(WorkflowDefinition? definition)
    {
        if (definition is null)
        {
            return definition;
        }
        if (resolveNodes is null)
        {
            string dedupe = "state.dedupe";
            NodeDefinition[] markers = definition.Nodes.Where(node => string.Equals(node.Type, dedupe, StringComparison.Ordinal)).ToArray();
            bool unresolved = markers.Any(node => node.Inputs?.Keys.Any(SettingPorts.IsSetting) == true)
                || definition.Wires.Any(wire => SettingPorts.IsSetting(wire.ToPort) && markers.Any(node => string.Equals(node.Id, wire.ToNode, StringComparison.Ordinal)));
            if (unresolved)
            {
                return null;
            }
            return definition;
        }
        IReadOnlyList<NodeDefinition>? nodes = resolveNodes(definition);
        if (nodes is null)
        {
            return null;
        }
        return definition with { Nodes = nodes };
    }
}
