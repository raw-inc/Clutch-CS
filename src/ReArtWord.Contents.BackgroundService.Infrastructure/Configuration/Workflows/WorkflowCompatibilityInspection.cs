using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

/// <summary>実装が宣言する動作契約と、保存した定義・状態の対応を変更前に確認します。</summary>
public sealed class WorkflowCompatibilityInspection(WorkflowStore workflows, SnapshotCatalog snapshots, WorkflowCompiler compiler, NodeRegistry registry,
    Func<string, CompiledWorkflow?> effective, string peerId)
{
    /// <summary>指定ワークフローと、このPeerの関連スナップショットをページ単位で検査します。</summary>
    /// <returns>値を変更せずに確認した問題と次ページ。</returns>
    public async Task<WorkflowCompatibilityReport> InspectAsync(WorkflowCompatibilityQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkflowId);
        if (query.Limit is < 1 or > 50)
        {
            throw new ArgumentException("一ページのスナップショット数は1から50を指定してください。", nameof(query));
        }
        StoredWorkflow? saved = await workflows.GetAsync(query.WorkflowId, cancellationToken).ConfigureAwait(false);
        CompiledWorkflow? active = effective(query.WorkflowId);
        List<WorkflowCompatibilityIssue> issues = [];
        if (saved is null && active is null)
        {
            issues.Add(new("workflow", null, null, null, null, null, [], new("workflow.missing", "このPeerで比較する保存版・有効版を取得できません。")));
        }
        InspectDefinition("saved", null, saved?.Definition, issues);
        InspectDefinition("effective", null, active?.Definition, issues);
        SnapshotRecord[] candidates = (await snapshots.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(snapshot => string.Equals(snapshot.PeerId, peerId, StringComparison.Ordinal) && Related(snapshot, query.WorkflowId)
                && (query.SnapshotAfter is null || 0 < string.CompareOrdinal(snapshot.Id, query.SnapshotAfter)))
            .OrderBy(snapshot => snapshot.Id, StringComparer.Ordinal).Take(query.Limit + 1).ToArray();
        foreach (SnapshotRecord snapshot in candidates.Take(query.Limit))
        {
            InspectSnapshot(snapshot, saved, active, issues);
        }
        string? next = null;
        if (query.Limit < candidates.Length)
        {
            next = candidates[query.Limit - 1].Id;
        }
        WorkflowCompatibilityReport report = new(query.WorkflowId, peerId, saved?.Revision, active?.Revision,
            issues.Take(100).ToArray(), 100 < issues.Count, Math.Min(query.Limit, candidates.Length), next);
        while (report.Issues.Count != 0 && 524288 < JsonSerializer.SerializeToUtf8Bytes(report, ContractJson.Options).Length)
        {
            report = report with { Issues = report.Issues.Take(report.Issues.Count - 1).ToArray(), IssuesOmitted = true };
        }
        return report;
    }

    /// <summary>不適合のノード、期待版、実装版と設定名を、値を含めずに列挙します。</summary>
    private void InspectDefinition(string area, string? snapshot, WorkflowDefinition? definition, List<WorkflowCompatibilityIssue> issues)
    {
        if (definition is null)
        {
            return;
        }
        CompilationResult compiled = compiler.Compile(definition);
        foreach (ValidationIssue issue in compiled.Issues)
        {
            if (100 < issues.Count)
            {
                return;
            }
            NodeDefinition? node = definition.Nodes.FirstOrDefault(item => string.Equals(item.Id, issue.NodeId, StringComparison.Ordinal));
            NodeDescriptor? descriptor = null;
            string[] settings = [];
            int? expected = null;
            if (node is not null)
            {
                descriptor = registry.Find(node.Type)?.Descriptor;
                expected = NodeBehaviorContracts.Expected(node);
                if (node.Settings.ValueKind == JsonValueKind.Object)
                {
                    settings = node.Settings.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
                }
            }
            issues.Add(new(area, snapshot, node?.Id, node?.Type, expected, descriptor?.ContractVersion, settings, issue));
        }
    }

    /// <summary>取得済み設定の期待版と、状態の利用条件を確認します。</summary>
    private void InspectSnapshot(SnapshotRecord snapshot, StoredWorkflow? saved, CompiledWorkflow? active, List<WorkflowCompatibilityIssue> issues)
    {
        try
        {
            if (!string.Equals(snapshot.Hash, ContractJson.Hash(snapshot.Value), StringComparison.Ordinal))
            {
                SnapshotIssue(snapshot, "snapshot.hashMismatch", "スナップショットの内容を検証できません。", issues);
                return;
            }
            if (snapshot.Kind == ESnapshotKind.Configuration)
            {
                ConfigurationSnapshotState state = snapshot.Value.Json.Deserialize<ConfigurationSnapshotState>(ContractJson.Options) ?? throw new JsonException();
                if (snapshot.NodeIds is not null)
                {
                    if (saved is null)
                    {
                        SnapshotIssue(snapshot, "snapshot.currentDefinitionMissing", "部分復元を比較する現在の設定がありません。", issues);
                        return;
                    }
                    state = SnapshotNodeSelection.Merge(new(saved, active?.Definition), state, snapshot.NodeIds);
                }
                InspectDefinition("snapshot.saved", snapshot.Id, state.Saved.Definition, issues);
                InspectDefinition("snapshot.effective", snapshot.Id, state.Effective, issues);
                return;
            }
            NodeStateSnapshot? values = snapshot.Value.Json.Deserialize<NodeStateSnapshot>(ContractJson.Options);
            CompiledWorkflow? resolved = null;
            if (active is not null)
            {
                resolved = compiler.Compile(active.Definition).Workflow;
            }
            if (values is null || resolved is null || !string.Equals(values.Contract, NodeStateContract.Fingerprint(resolved.ExecutionDefinition with { Nodes = resolved.OrderedNodes }), StringComparison.Ordinal))
            {
                SnapshotIssue(snapshot, "snapshot.stateContract", "現在の有効定義と、状態を取得したときの利用条件が一致しません。state系ノードの動作契約と重複抑止設定、または一緒に復元する設定を確認してください。", issues);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            SnapshotIssue(snapshot, "snapshot.compatibilityUnknown", "スナップショットの形式または比較条件を確認できません。", issues);
        }
    }

    /// <summary>対象ワークフローに属することを確認できるスナップショットだけを扱います。</summary>
    /// <returns>設定または宣言されたワークフロー状態の対象か。</returns>
    private static bool Related(SnapshotRecord snapshot, string workflow)
        => snapshot.Kind == ESnapshotKind.Configuration && string.Equals(snapshot.TargetId, workflow, StringComparison.Ordinal)
            || snapshot.Kind == ESnapshotKind.Runtime && NodeStateScope.TryGetWorkflowId(snapshot.TargetId, snapshot.PeerId, out string? owner) && string.Equals(owner, workflow, StringComparison.Ordinal);

    /// <summary>状態全体の問題を、取得元を確認できるスナップショットID付きで残します。</summary>
    private static void SnapshotIssue(SnapshotRecord snapshot, string code, string message, List<WorkflowCompatibilityIssue> issues)
    {
        if (issues.Count <= 100)
        {
            issues.Add(new("snapshot.state", snapshot.Id, null, null, null, null, [], new(code, message)));
        }
    }
}
