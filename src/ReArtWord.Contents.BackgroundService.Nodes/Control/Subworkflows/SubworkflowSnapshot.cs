using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;

/// <summary>一回の呼出しが参照する子定義を先に固定し、再帰と展開上限を副作用前に検証します。</summary>
internal static class SubworkflowSnapshot
{
    /// <summary>孫以降も含めた同じ開始時のカタログから不変の定義を組み立てます。</summary>
    /// <returns>最大8段・4096ノード・1MiBの展開です。</returns>
    internal static WorkflowDefinition Capture(WorkflowDefinition definition, IReadOnlyDictionary<string, WorkflowDefinition> available, NodeRegistry registry)
    {
        HashSet<string> stack = new(StringComparer.Ordinal);
        int count = 0;
        WorkflowDefinition captured = Visit(definition, available, registry, stack, ref count);
        if (1048576 < JsonSerializer.SerializeToUtf8Bytes(captured, ContractJson.Options).Length)
        {
            throw new FormatException("subworkflow.snapshot.limit");
        }
        return captured;
    }

    /// <summary>同じ定義への重複呼出しは許可し、呼出し経路上の再帰だけを拒否します。</summary>
    /// <returns>一つの子とその内側を固定した定義です。</returns>
    private static WorkflowDefinition Visit(WorkflowDefinition definition, IReadOnlyDictionary<string, WorkflowDefinition> available, NodeRegistry registry, HashSet<string> stack, ref int count)
    {
        if (8 <= stack.Count || !stack.Add(definition.Id))
        {
            throw new FormatException("subworkflow.recursion");
        }
        _ = SubworkflowInterface.Read(definition);
        List<NodeDefinition> nodes = [];
        foreach (NodeDefinition node in definition.Nodes)
        {
            count++;
            if (4096 < count)
            {
                throw new FormatException("subworkflow.snapshot.limit");
            }
            NodeDefinition captured = node;
            if (SubworkflowInterface.Target(node.Type) is { } target)
            {
                WorkflowDefinition? child = SubworkflowInterface.Captured(node) ?? available.GetValueOrDefault(target);
                if (child is null || !string.Equals(child.Id, target, StringComparison.Ordinal))
                {
                    throw new FormatException("subworkflow.target.unavailable");
                }
                WorkflowDefinition next = Visit(child, available, registry, stack, ref count);
                JsonObject settings = JsonNode.Parse(node.Settings.GetRawText())!.AsObject();
                settings[SubworkflowInterface.CapturedDefinition] = JsonSerializer.SerializeToNode(next, ContractJson.Options);
                captured = node with { Settings = JsonSerializer.SerializeToElement(settings, ContractJson.Options) };
            }
            nodes.Add(captured);
        }
        stack.Remove(definition.Id);
        return definition with { Nodes = nodes };
    }

}
