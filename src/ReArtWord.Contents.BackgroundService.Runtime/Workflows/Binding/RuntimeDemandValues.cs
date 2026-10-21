using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Retention;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;

/// <summary>起動済みの利用側に不足する実行値だけを、一回の要求範囲で評価します。</summary>
internal sealed class RuntimeDemandValues(NodeRegistry registry, CompiledWorkflow workflow,
    Func<string, Task<ExecutionFrame>?> read, Func<NodeDefinition, ExecutionFrame[], bool, Task<ExecutionFrame>> execute,
    NodeOutputRetention retention, Func<string, Task<ExecutionFrame>?>? inherited = null)
{
    private readonly Dictionary<string, ExecutionFrame> evaluated = new(StringComparer.Ordinal);
    private readonly HashSet<string> visiting = new(StringComparer.Ordinal);

    /// <summary>開始条件を満たす利用側にだけ値を補い、待受や明示信号待ちを自動起動しません。</summary>
    /// <returns>この利用側にだけ供給する親結果です。</returns>
    internal async Task<ExecutionFrame[]> SupplyAsync(NodeDefinition consumer, ExecutionFrame[] parents, bool entry, CancellationToken cancellationToken = default)
    {
        Dictionary<string, ExecutionFrame> frames = parents.ToDictionary(frame => frame.NodeId, StringComparer.Ordinal);
        if (!entry && PureValueInputs.CheckActivation(registry, consumer, workflow, frames) is not null)
        {
            return parents;
        }
        foreach (ExecutionFrame parent in parents)
        {
            bool toggle = consumer.Type is "control.toggleButton";
            if (Inactive(parent) && NeedsValue(consumer, parent) && await ResolveAsync(parent.NodeId, branchRead: toggle, cancellationToken: cancellationToken).ConfigureAwait(false) is { } value)
            {
                frames[parent.NodeId] = value;
            }
        }
        return frames.Values.ToArray();
    }

    /// <summary>既に届いた実行結果や開始信号を、過去の値や新しい実行で置き換えません。</summary>
    /// <returns>未供給のデータ出力を実際に要求している場合だけtrueです。</returns>
    private bool NeedsValue(NodeDefinition consumer, ExecutionFrame parent)
    {
        string result = "result";
        NodeDescriptor? descriptor = registry.Find(workflow.FindNode(parent.NodeId)!.Type)?.Descriptor;
        foreach (WireDefinition wire in workflow.Incoming[consumer.Id].Where(wire => string.Equals(wire.FromNode, parent.NodeId, StringComparison.Ordinal)))
        {
            if (string.Equals(wire.FromPort, result, StringComparison.Ordinal) || StartSignalPorts.IsOutput(descriptor, wire.FromPort))
            {
                continue;
            }
            IReadOnlyDictionary<string, NodeValue> supplied = WireInputs.Create(consumer with { Inputs = null }, string.Empty, [wire],
                new Dictionary<string, ExecutionFrame>(StringComparer.Ordinal) { [parent.NodeId] = parent }, null, descriptor: registry.Find(consumer.Type)?.Descriptor);
            if (!supplied.ContainsKey(wire.ToPort))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>通常評価の成否を優先し、未起動の値依存だけを遡ります。</summary>
    /// <returns>構築した値、または自動評価の対象外ならnullです。</returns>
    internal async Task<ExecutionFrame?> ResolveAsync(string id, bool branchRead = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (evaluated.TryGetValue(id, out ExecutionFrame? cached))
        {
            return cached;
        }
        if (64 <= visiting.Count || !visiting.Add(id))
        {
            return null;
        }
        try
        {
            Task<ExecutionFrame>? currentRequest = inherited?.Invoke(id);
            Task<ExecutionFrame>? previous = currentRequest ?? read(id);
            ExecutionFrame? frame = previous is null ? null
                                                     : await previous.ConfigureAwait(false);
            if (frame is not null && !Inactive(frame))
            {
                return frame;
            }
            NodeDefinition node = workflow.FindNode(id)!;
            INode implementation = registry.Find(node.Type)!;
            bool merge = ValueMergePorts.IsNode(node.Type);
            bool canSupply = implementation is IActionNode && node.Type is not "control.button" && !WorkflowRegionNodeTypes.IsStart(node.Type) && !WorkflowRegionNodeTypes.IsEnd(node.Type) && (!EventMergePorts.IsNode(node.Type) || merge);
            string trigger = "trigger";
            bool toggle = node.Type is "control.toggleButton";
            if (!canSupply || branchRead && implementation is not IRuntimeValueNode && !toggle && !merge && !Conditional(node.Type))
            {
                return frame;
            }
            WireDefinition[] triggers = workflow.Incoming[id].Where(wire => string.Equals(wire.ToPort, trigger, StringComparison.Ordinal)).ToArray();
            bool conditionalValue = branchRead && !workflow.PcSelectionValues.Contains(id) && implementation is IValueNode && triggers.All(wire => Conditional(workflow.FindNode(wire.FromNode)!.Type));
            if (!toggle && !conditionalValue && triggers.Length != 0)
            {
                if (workflow.PcSelectionValues.Contains(id))
                {
                    return frame;
                }
                ExecutionFrame? retained = await retention.GetAsync(workflow, id, cancellationToken).ConfigureAwait(false);
                if (retained is not null)
                {
                    evaluated[id] = retained;
                }
                return retained ?? frame;
            }
            if (workflow.ConfigurationValues?.GetValueOrDefault(id) is { Outcome: EOutcome.Succeeded } configured)
            {
                return new(id, configured.Outcome, [new("configuration", configured)], ConfigurationOnly: true);
            }
            List<ExecutionFrame> parents = [];
            foreach (string parent in workflow.Parents(id))
            {
                ExecutionFrame? value;
                if (toggle && workflow.Incoming[id].Where(wire => string.Equals(wire.FromNode, parent, StringComparison.Ordinal)).All(wire => string.Equals(wire.ToPort, trigger, StringComparison.Ordinal)))
                {
                    Task<ExecutionFrame>? observed = read(parent);
                    value = observed is null ? null
                                             : await observed.ConfigureAwait(false);
                }
                else
                {
                    value = await ResolveAsync(parent, branchRead || merge || toggle, cancellationToken).ConfigureAwait(false);
                }
                if (value is null)
                {
                    if (!merge && !toggle)
                    {
                        return frame;
                    }
                    value = WorkflowEventDispatcher.Placeholder(parent);
                }
                parents.Add(value);
            }
            if (RecipeIterationInputs.Check(workflow, id, parents).Missing)
            {
                return frame;
            }
            Dictionary<string, ExecutionFrame> supplied = parents.ToDictionary(parent => parent.NodeId, StringComparer.Ordinal);
            IReadOnlyDictionary<string, NodeValue> inputs = PureValueInputs.Create(registry, node, string.Empty, workflow, supplied, null, ignoreTrigger: toggle).Inputs;
            if (merge && !inputs.Keys.Any(port => 0 < ConcatPorts.Index(port)))
            {
                return frame ?? WorkflowEventDispatcher.Placeholder(id);
            }
            string off = "offValue", on = "onValue";
            if (!merge && workflow.Incoming[id].Any(wire => (!toggle || !string.Equals(wire.ToPort, trigger, StringComparison.Ordinal)
                && !string.Equals(wire.ToPort, off, StringComparison.Ordinal) && !string.Equals(wire.ToPort, on, StringComparison.Ordinal)) && !inputs.ContainsKey(wire.ToPort)
                && !Contracts.Values.DefaultIfNullValues.IsUnusedFallback(node.Type, wire.ToPort, inputs)))
            {
                return frame;
            }
            ExecutionFrame result = await execute(node, parents.ToArray(), branchRead || merge).ConfigureAwait(false);
            evaluated[id] = result;
            return result;
        }
        finally
        {
            visiting.Remove(id);
        }
    }

    /// <summary>実際の失敗・取消・結果不明・明示的な非選択は再実行対象にしません。</summary>
    /// <returns>データや起動契機がなく未起動だった場合だけtrueです。</returns>
    private static bool Inactive(ExecutionFrame frame) => frame.Outcome == EOutcome.NotExecuted && !frame.ConfigurationOnly
        && frame.Targets.Count != 0 && frame.Targets.All(target => target.Result.ReasonCode is "input.inactive" or "input.missing" or "input.eventPending" or "signal.eventBoundary");

    /// <summary>値選択の条件だけを読み、一般の信号待ち処理を逆向きに起動しません。</summary>
    /// <returns>副作用を持たない条件ゲートかです。</returns>
    private static bool Conditional(string type) => type is "control.ifStart" or "control.ifNotStart" or "control.if";
}
