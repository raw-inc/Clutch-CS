using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

/// <summary>レシピを配置先のグラフへ展開し、別WF実行へ制御を移しません。</summary>
internal sealed class SubworkflowExpansion(NodeRegistry registry, string workflowId)
{
    private readonly List<NodeDefinition> nodes = [];
    private readonly List<WireDefinition> wires = [];
    private readonly List<WorkflowRecipeInstance> instances = [];
    private readonly Dictionary<string, IReadOnlyList<string>> recipeInputs = new(StringComparer.Ordinal);
    private readonly HashSet<string> visiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkflowDefinition> definitions = new(StringComparer.Ordinal);
    private bool personal;

    /// <summary>実行や必須入力の供給を伴わず、保存済み配置の内部構成だけを展開します。</summary>
    /// <returns>実行用と同じ識別子・配線・上限を使う読み取り用レイアウトです。</returns>
    internal static IReadOnlyList<WorkflowRecipeInstance> Inspect(WorkflowDefinition definition, NodeRegistry registry)
    {
        SubworkflowExpansion expansion = new(registry, definition.Id);
        expansion.Visit(definition, null, null);
        if (1048576 < JsonSerializer.SerializeToUtf8Bytes(expansion.instances, ContractJson.Options).Length)
        {
            throw new FormatException("Recipe preview exceeds the 1 MiB limit.");
        }
        return expansion.instances.ToArray();
    }

    /// <summary>公開契約と展開上限を確認してから通常のコンパイラーへ渡します。</summary>
    /// <returns>保存元と展開後を分離した不変の実行計画です。</returns>
    internal static CompilationResult Compile(WorkflowDefinition definition, NodeRegistry registry,
        Func<WorkflowDefinition, IReadOnlyDictionary<string, IReadOnlyList<string>>, CompilationResult> compile)
    {
        try
        {
            SubworkflowExpansion expansion = new(registry, definition.Id);
            expansion.Visit(definition, null, null);
            WorkflowDefinition expanded = definition with { Nodes = expansion.nodes.ToArray(), Wires = expansion.wires.ToArray(), ContainsPersonalData = definition.ContainsPersonalData || expansion.personal };
            if (1048576 < JsonSerializer.SerializeToUtf8Bytes(expanded, ContractJson.Options).Length)
            {
                throw new FormatException("Recipe expansion exceeds the 1 MiB limit.");
            }
            CompilationResult result = compile(expanded, expansion.recipeInputs);
            if (result.Workflow is not null)
            {
                return result with { Workflow = result.Workflow with { Definition = ContractJson.Copy(definition), Revision = ContractJson.Hash(definition), ExpandedDefinition = expanded, RecipeInstances = expansion.instances.ToArray() } };
            }
            return result;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return new(null, [new("subworkflow.expansion.invalid", "The recipe cannot be expanded. Check its references, public ports, recursive references and expansion limits.")]);
        }
    }

    /// <summary>一つの配置ごとに境界と内部ノードの所有IDを確定します。</summary>
    /// <returns>親から配線するための公開境界です。</returns>
    private Layer Visit(WorkflowDefinition definition, string? instance, NodeDefinition? placement)
    {
        if (8 < visiting.Count || !visiting.Add(definition.Id))
        {
            throw new FormatException("Recursive recipe reference.");
        }
        personal |= definition.ContainsPersonalData;
        Dictionary<string, string> ids = definition.Nodes.ToDictionary(node => node.Id, node => instance is null ? node.Id
                                                                                                                 : RecipeNodeIdentity.Child(instance, node.Id), StringComparer.Ordinal);
        Dictionary<string, Layer> groups = new(StringComparer.Ordinal);
        List<NodeDefinition> layout = [];
        int first = nodes.Count;
        foreach (NodeDefinition node in definition.Nodes)
        {
            NodeDefinition copy = node with { Id = ids[node.Id], Target = node.Target ?? placement?.Target, Safety = node.Safety ?? placement?.Safety };
            string toggle = "control.toggleButton";
            if (instance is not null && string.Equals(node.Type, toggle, StringComparison.Ordinal))
            {
                copy = copy with { Settings = ToggleButtonSource.Capture(copy.Settings, definition.Id, node.Id), ContractVersion = 2 };
            }
            layout.Add(copy);
            if (SubworkflowInterface.Target(node.Type) is { } target)
            {
                WorkflowDefinition child = Resolve(node, target);
                groups[node.Id] = Visit(child, copy.Id, copy);
                continue;
            }
            if (SubworkflowInterface.IsBoundary(node.Type) && instance is not null)
            {
                copy = copy with { Type = SubworkflowInterface.PortType, Target = null, Safety = null };
                string inputType = SubworkflowInterface.InputType;
                if (string.Equals(node.Type, inputType, StringComparison.Ordinal))
                {
                    copy = copy with { Inputs = null };
                    SubworkflowField field = SubworkflowInterface.ReadField(node);
                    if (placement?.Inputs?.TryGetValue(field.Name, out NodeValue? value) == true)
                    {
                        copy = copy with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = value.Copy() } };
                    }
                }
            }
            nodes.Add(copy);
            if (4096 < nodes.Count)
            {
                throw new FormatException("Recipe node limit exceeded.");
            }
        }
        foreach (WireDefinition wire in definition.Wires)
        {
            (string from, string port) = Source(wire, ids, groups);
            if (!groups.TryGetValue(wire.ToNode, out Layer? group))
            {
                wires.Add(wire with { FromNode = from, FromPort = port, ToNode = ids[wire.ToNode] });
                continue;
            }
            Connect(wire with { FromNode = from, FromPort = port }, group);
        }
        NodeDefinition[] members = nodes.Skip(first).ToArray();
        string[] entries = Entries(members);
        Layer result = new(new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal), entries, members.Where(node => registry.Find(node.Type) is not IValueNode).Select(node => node.Id).ToArray(), placement);
        if (instance is not null)
        {
            SubworkflowInterface contract = SubworkflowInterface.Read(definition);
            result = result with { Inputs = contract.Inputs.ToDictionary(field => field.Name, field => ids[field.NodeId], StringComparer.Ordinal), Outputs = contract.Outputs.ToDictionary(field => field.Name, field => ids[field.NodeId], StringComparer.Ordinal) };
            ValidateArguments(placement!, result);
            foreach (string entry in result.Entries.Where(id => registry.Find(nodes.First(node => string.Equals(node.Id, id, StringComparison.Ordinal)).Type) is IActionNode and not IValueNode))
            {
                // 反復内の公開入力を内部入口へ引き継ぐ候補です。採用範囲は反復コンパイラーが確定します。
                recipeInputs[entry] = (recipeInputs.GetValueOrDefault(entry) ?? []).Concat(result.Inputs.Values).Distinct(StringComparer.Ordinal).ToArray();
            }
            instances.Add(new(instance, definition.Id, definition with { Id = workflowId, Nodes = layout, Wires = definition.Wires.Select(wire => wire with { FromNode = ids[wire.FromNode], ToNode = ids[wire.ToNode] }).ToArray() }));
        }
        visiting.Remove(definition.Id);
        return result;
    }

    /// <summary>同じ展開中は同じ再利用定義を参照します。</summary>
    /// <returns>実行開始後に変化しない定義です。</returns>
    private WorkflowDefinition Resolve(NodeDefinition node, string target)
    {
        WorkflowDefinition? captured = SubworkflowInterface.Captured(node);
        if (captured is not null)
        {
            if (!string.Equals(captured.Id, target, StringComparison.Ordinal))
            {
                throw new FormatException("Recipe target mismatch.");
            }
            return captured;
        }
        if (!definitions.TryGetValue(target, out WorkflowDefinition? definition))
        {
            definition = registry.Dynamic?.Definition(target) ?? throw new FormatException("Recipe is unavailable.");
            definitions[target] = ContractJson.Copy(definition);
        }
        return definitions[target];
    }

    /// <summary>出力境界を通すことでPathを二回適用しません。</summary>
    /// <returns>展開後の出力端点です。</returns>
    private static (string Node, string Port) Source(WireDefinition wire, Dictionary<string, string> ids, Dictionary<string, Layer> groups)
    {
        if (groups.TryGetValue(wire.FromNode, out Layer? group))
        {
            return (group.Outputs.GetValueOrDefault(wire.FromPort) ?? throw new FormatException("Expose the internal output as a public recipe output."), "value");
        }
        return (ids[wire.FromNode], wire.FromPort);
    }

    /// <summary>公開入力、起動信号、配置側の実行PCを内部へ接続します。</summary>
    private void Connect(WireDefinition wire, Layer group)
    {
        if (group.Inputs.TryGetValue(wire.ToPort, out string? input))
        {
            wires.Add(wire with { ToNode = input, ToPort = "value" });
            return;
        }
        string trigger = "trigger";
        if (string.Equals(wire.ToPort, trigger, StringComparison.Ordinal))
        {
            wires.AddRange(group.Entries.Select(id => wire with { ToNode = id }));
            return;
        }
        if (!string.Equals(wire.ToPort, PcSelectionPorts.Execution, StringComparison.Ordinal) || group.Placement?.Target is not null)
        {
            throw new FormatException("Unknown or conflicting recipe input.");
        }
        foreach (string id in group.Targets)
        {
            int index = nodes.FindIndex(node => string.Equals(node.Id, id, StringComparison.Ordinal));
            if (nodes[index].Target is null && !wires.Any(item => string.Equals(item.ToNode, id, StringComparison.Ordinal) && string.Equals(item.ToPort, PcSelectionPorts.Execution, StringComparison.Ordinal)))
            {
                nodes[index] = nodes[index] with { PcExecution = group.Placement!.PcExecution };
                wires.Add(wire with { ToNode = id });
            }
        }
    }

    /// <summary>グループ起動信号を値構築で消さず、内部の実処理入口へ渡します。</summary>
    /// <returns>純粋値だけのグループでは公開出力境界を入口とします。</returns>
    private string[] Entries(NodeDefinition[] members)
    {
        HashSet<string> pure = members.Where(node => registry.Find(node.Type) is IValueNode).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        string[] entries = members.Where(node => !pure.Contains(node.Id) && wires.Where(wire => string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal)).All(wire => pure.Contains(wire.FromNode))).Select(node => node.Id).ToArray();
        if (entries.Length == 0)
        {
            entries = members.Where(node => !wires.Any(wire => string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal))).Select(node => node.Id).ToArray();
        }
        return entries;
    }

    /// <summary>配線に変換できない定数入力を黙って捨てません。</summary>
    private void ValidateArguments(NodeDefinition placement, Layer layer)
    {
        foreach ((string name, NodeValue value) in placement.Inputs ?? new Dictionary<string, NodeValue>(StringComparer.Ordinal))
        {
            if (layer.Inputs.ContainsKey(name))
            {
                continue;
            }
            string trigger = "trigger";
            if (!string.Equals(name, trigger, StringComparison.Ordinal))
            {
                throw new FormatException("Unknown recipe argument.");
            }
            foreach (string id in layer.Entries)
            {
                int index = nodes.FindIndex(node => string.Equals(node.Id, id, StringComparison.Ordinal));
                Dictionary<string, NodeValue> inputs = nodes[index].Inputs?.ToDictionary(StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
                inputs[trigger] = value.Copy();
                nodes[index] = nodes[index] with { Inputs = inputs };
            }
        }
    }

    /// <summary>一つの配置の接続可能な端点です。</summary>
    private sealed record Layer(Dictionary<string, string> Inputs, Dictionary<string, string> Outputs, string[] Entries, string[] Targets, NodeDefinition? Placement);
}
