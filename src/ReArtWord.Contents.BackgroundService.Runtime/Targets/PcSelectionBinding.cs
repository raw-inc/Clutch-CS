using System.Collections.Frozen;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Runtime.Targets;

/// <summary>PC選択を通常データの流れから分離し、保存版に対応した条件へ固定します。</summary>
internal static class PcSelectionBinding
{
    /// <summary>専用ポートの値を検証し、実行計画へ保持します。</summary>
    /// <returns>PC選択付きの計画、または不成立の接続と理由。</returns>
    internal static CompilationResult Bind(CompiledWorkflow original, CompiledWorkflow bound, NodeRegistry registry, IReadOnlySet<string>? publicValues = null)
    {
        Dictionary<string, IReadOnlyList<PcSelection>> selections = new(StringComparer.Ordinal);
        List<ValidationIssue> issues = [];
        HashSet<string> dynamic = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<WireDefinition>> incoming = new(bound.Incoming, StringComparer.Ordinal);
        Contracts.Values.WorkflowTypeInference types = new(original.Definition, registry.Descriptors);
        IReadOnlySet<string> providers = bound.OrderedNodes.Where(node => string.Equals(types.PortType(node.Id, "value", true)?.Type, PcSelectionPorts.TypeName, StringComparison.Ordinal)).Select(node => node.Id).ToFrozenSet(StringComparer.Ordinal);
        foreach (NodeDefinition node in bound.OrderedNodes)
        {
            WireDefinition[] wires = original.Incoming[node.Id].Where(wire => string.Equals(wire.ToPort, PcSelectionPorts.Execution, StringComparison.Ordinal)).ToArray();
            if (!Enum.IsDefined(node.PcExecution) || (node.PcExecution == EPcExecutionMode.All && wires.Length == 0))
            {
                issues.Add(new("selection.mode", "全候補PCで実行する場合は、実行PC入力へ選択ノードを接続してください。", node.Id));
            }
            if (wires.Length == 0)
            {
                continue;
            }
            if (publicValues is not null && wires.Any(wire => publicValues.Contains(wire.FromNode)))
            {
                continue;
            }
            if (node.Target is not null || registry.Find(node.Type) is IValueNode)
            {
                issues.Add(new("selection.conflictingTarget", "従来の実行先指定との併用、または選択値自身への実行先指定はできません。", node.Id));
                continue;
            }
            if (wires.Any(wire => bound.ConfigurationValues?.ContainsKey(wire.FromNode) != true))
            {
                if (registry.Find(node.Type) is ISourceNode)
                {
                    issues.Add(new("selection.sourceStatic", "継続待受のPC候補は開始前に確定する選択へ接続してください。", node.Id));
                    continue;
                }
                dynamic.Add(node.Id);
                incoming[node.Id] = [.. bound.Incoming[node.Id], .. wires];
                continue;
            }
            List<PcSelection> combined = [];
            try
            {
                foreach (WireDefinition wire in wires)
                {
                    NodeResult? value = bound.ConfigurationValues?.GetValueOrDefault(wire.FromNode);
                    if (value?.Outcome != EOutcome.Succeeded || !value.Outputs.TryGetValue(wire.FromPort, out NodeValue? output))
                    {
                        throw new FormatException("PC選択の条件を構築できません。");
                    }
                    combined.AddRange(PcSelectionValue.Read(output));
                }
                _ = PcSelectionValue.Read(NodeValue.FromObject(combined));
                selections[node.Id] = combined.AsReadOnly();
            }
            catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or ArgumentException)
            {
                issues.Add(new("selection.binding", "実行PC入力には、正しく構築されたPC選択条件が必要です。", node.Id));
            }
        }
        if (issues.Count != 0)
        {
            return new(null, issues);
        }
        IReadOnlyDictionary<string, string> revisions = selections.ToFrozenDictionary(pair => pair.Key, pair =>
        {
            NodeDefinition node = bound.FindNode(pair.Key)!;
            return ContractJson.Hash(new { Node = node with { Position = null, Size = null, LogicalName = null, Safety = null, StartOnLoad = false, ContractVersion = NodeBehaviorContracts.CanonicalVersion(node) }, Choices = pair.Value });
        }, StringComparer.Ordinal);
        return new(bound with { PcSelections = selections.ToFrozenDictionary(StringComparer.Ordinal), PcSelectionRevisions = revisions,
            Incoming = incoming.ToFrozenDictionary(StringComparer.Ordinal), DynamicPcSelections = dynamic.ToFrozenSet(StringComparer.Ordinal), PcSelectionValues = providers }, []);
    }
}
