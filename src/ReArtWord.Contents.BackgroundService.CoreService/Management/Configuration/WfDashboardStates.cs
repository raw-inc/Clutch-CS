using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;

/// <summary>配置したカードの小さい表示情報だけを取得し、WF本文の反復転送を避けます。</summary>
internal sealed class WfDashboardStates(ServiceRuntime service, WfDashboardValues values)
{
    private readonly ConcurrentDictionary<WfDashboardReference, CachedState> cache = new();

    /// <summary>有効版変更では即時、それ以外は一秒で更新する有限の表示キャッシュです。</summary>
    /// <returns>元ノードを実行せず取得した状態です。</returns>
    internal async Task<IReadOnlyList<WfDashboardNodeState>> ReadAsync(WfDashboardNodeQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query.References);
        if (64 < query.References.Count)
        {
            throw new ArgumentException("Invalid WFDashboard references.", nameof(query));
        }
        WfDashboardReference[] references = query.References.Distinct().ToArray();
        foreach (WfDashboardReference reference in references)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ArgumentException.ThrowIfNullOrWhiteSpace(reference.WorkflowId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reference.NodeId);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(reference.WorkflowId.Length, 256);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(reference.NodeId.Length, 256);
            if (!WfDashboardReference.Supports(reference.NodeType))
            {
                throw new ArgumentException("Unsupported WFDashboard node type.", nameof(query));
            }
        }
        List<WfDashboardNodeState> result = [];
        foreach (IGrouping<string, WfDashboardReference> group in references.GroupBy(reference => reference.WorkflowId, StringComparer.Ordinal))
        {
            result.AddRange(await ReadWorkflowAsync(group.Key, group.ToArray(), cancellationToken).ConfigureAwait(false));
        }
        Contracts.Execution.Concurrency.WorkflowConcurrencyTarget[] limits = result.Where(state => state.Problem is null && ConcurrencyNodeTypes.IsStart(state.Reference.NodeType))
            .Select(state => new Contracts.Execution.Concurrency.WorkflowConcurrencyTarget(state.Reference.WorkflowId, state.Reference.NodeId)).ToArray();
        if (limits.Length != 0)
        {
            IReadOnlyList<Contracts.Execution.Concurrency.WorkflowConcurrencySnapshot> queues = await service.Concurrency.ReadAsync(limits, cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < result.Count; index++)
            {
                WfDashboardReference reference = result[index].Reference;
                result[index] = result[index] with { Concurrency = queues.FirstOrDefault(queue => string.Equals(queue.WorkflowId, reference.WorkflowId, StringComparison.Ordinal)
                    && string.Equals(queue.NodeId, reference.NodeId, StringComparison.Ordinal)) };
            }
        }
        return result;
    }

    /// <summary>保存指紋が同じなら本文を再復号せず、有限のカード投影だけを再利用します。</summary>
    /// <returns>表示名と固定型の状態です。未反映の保存や削除も指紋で検出します。</returns>
    private async Task<IReadOnlyList<WfDashboardNodeState>> ReadWorkflowAsync(string workflow, WfDashboardReference[] references, CancellationToken cancellationToken = default)
    {
        string? active = service.Sources.GetEffective(workflow)?.Revision;
        CachedState?[] previous = references.Select(reference => cache.GetValueOrDefault(reference)).ToArray();
        bool detached = service.Cluster.IsDetached;
        if (previous.All(item => item is not null && item.Detached == detached && DateTimeOffset.UtcNow < item.Expires && string.Equals(item.EffectiveRevision, active, StringComparison.Ordinal)))
        {
            return previous.Select(item => item!.State).ToArray();
        }
        StoredWorkflow? source = null;
        string? fingerprint = null;
        bool unchanged = false;
        if (detached)
        {
            source = await service.Workflows.GetAsync(workflow, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Dictionary<string, string> known = new(StringComparer.Ordinal);
            string? previousFingerprint = previous[0]?.Fingerprint;
            if (previousFingerprint is not null && previous.All(item => item is not null && !item.Detached && string.Equals(item.Fingerprint, previousFingerprint, StringComparison.Ordinal)))
            {
                known.Add(workflow, previousFingerprint);
            }
            IReadOnlyList<DurableObservation> observed = await service.Repository.ObserveAsync("workflows", known, new HashSet<string>(StringComparer.Ordinal) { workflow }, cancellationToken).ConfigureAwait(false);
            if (observed.Count != 0)
            {
                DurableObservation observation = observed[0];
                fingerprint = observation.Fingerprint;
                unchanged = observation.Entry is null && string.Equals(known.GetValueOrDefault(workflow), fingerprint, StringComparison.Ordinal);
                source = observation.Entry?.Value?.Json.Deserialize<StoredWorkflow>();
            }
        }
        List<WfDashboardNodeState> result = [];
        for (int index = 0; index < references.Length; index++)
        {
            WfDashboardReference reference = references[index];
            WfDashboardNodeState state = Build(reference, source, active);
            if (unchanged)
            {
                state = previous[index]!.State;
                state = state with { Active = state.Revision is not null && string.Equals(state.Revision, active, StringComparison.Ordinal) };
            }
            if (256 <= cache.Count && !cache.ContainsKey(reference))
            {
                foreach (WfDashboardReference expired in cache.Keys.Take(1))
                {
                    cache.TryRemove(expired, out _);
                }
            }
            cache[reference] = new(state, active, DateTimeOffset.UtcNow.AddSeconds(1), fingerprint, detached);
            result.Add(state);
        }
        return result;
    }

    /// <summary>保存版の名称・ラベルと、固定型の編集欄だけを投影します。</summary>
    /// <returns>元WF全体や後続出力を含まない表示です。</returns>
    private WfDashboardNodeState Build(WfDashboardReference reference, StoredWorkflow? source, string? active)
    {
        NodeDefinition? node = reference.Find(source?.Definition);
        if (node is null || source is null)
        {
            return new(reference, string.Empty, reference.NodeId, null, false, Problem: "The referenced node is missing or its type changed.");
        }
        string title = node.LogicalName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = service.Registry.Find(node.Type)?.Descriptor.DisplayName ?? node.Type;
        }
        NodeValue? label = new WorkflowSettingValues(source.Definition).Input(node, "label");
        WfDashboardValues.ValueState? value = values.Read(source.Definition, node);
        string? problem = null;
        string valueType = "value.constant";
        if (string.Equals(node.Type, valueType, StringComparison.Ordinal) && value is null)
        {
            problem = "This Value is unavailable or its type cannot be fixed. Edit it in the source workflow.";
        }
        string? buttonLabel = null;
        if (label?.Json.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            buttonLabel = Short(label.GetString()!);
        }
        return new(reference, Short(source.Definition.Name), Short(title), source.Revision, string.Equals(source.Revision, active, StringComparison.Ordinal),
            buttonLabel, value?.Text, value?.Type.Type, problem);
    }

    /// <summary>カードの見出しだけを有限にします。</summary>
    /// <returns>元の識別情報と分離した表示名です。</returns>
    private static string Short(string value)
    {
        if (256 < value.Length)
        {
            return value[..256] + "…";
        }
        return value;
    }

    /// <summary>本文を含めない一秒間の表示スナップショットです。</summary>
    private sealed record CachedState(WfDashboardNodeState State, string? EffectiveRevision, DateTimeOffset Expires, string? Fingerprint, bool Detached);
}
