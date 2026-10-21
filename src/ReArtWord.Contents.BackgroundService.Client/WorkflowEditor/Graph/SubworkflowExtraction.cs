using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>選択範囲を公開境界付きレシピと置換後グラフへ変換します。</summary>
internal sealed class SubworkflowExtraction(WorkflowDefinition original, IReadOnlySet<string> selected, IReadOnlyList<NodeDescriptor> catalog, UiText text)
{
    private readonly List<NodeDefinition> boundaries = [];
    private readonly List<WireDefinition> inside = [], outside = [];
    private readonly HashSet<string> inputNames = new(StringComparer.Ordinal), outputNames = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Node, string Port), string> inputPorts = [];
    private readonly GraphTypeInference types = new(original, catalog);
    private readonly string instanceId = "recipe-" + Guid.NewGuid().ToString("N")[..8];
    private double outputX, inputY = 80, outputY = 80;

    /// <summary>内部の値・配線条件を変えず、境界をまたぐ線だけを分割します。</summary>
    /// <returns>適用前に保存とロックを確認できる変換結果です。</returns>
    internal GraphExtractionPlan Create()
    {
        NodeDefinition[] nodes = original.Nodes.Where(node => selected.Contains(node.Id)).ToArray();
        if (nodes.Length == 0 || nodes.Any(node => types.Descriptor(node.Id) is null))
        {
            throw UiError.InvalidOperation("Select nodes with available recipes before extracting a SubWorkflow.");
        }
        ValidateLoops();
        double left = nodes.Min(node => (node.Position ?? new(60, 80)).X), top = nodes.Min(node => (node.Position ?? new(60, 80)).Y);
        NodeDefinition[] copied = nodes.Select(node => node with { Position = new(420 + (node.Position ?? new(60, 80)).X - left, 80 + (node.Position ?? new(60, 80)).Y - top) }).ToArray();
        outputX = copied.Max(node => node.Position!.X + (node.Size?.Width ?? 290)) + 140;
        inside.AddRange(original.Wires.Where(wire => selected.Contains(wire.FromNode) && selected.Contains(wire.ToNode)));
        outside.AddRange(original.Wires.Where(wire => !selected.Contains(wire.FromNode) && !selected.Contains(wire.ToNode)));
        foreach (NodeDefinition node in nodes.Where(node => SubworkflowInterface.IsBoundary(node.Type)))
        {
            SubworkflowField field = SubworkflowInterface.ReadField(node);
            bool input = string.Equals(node.Type, SubworkflowInterface.InputType, StringComparison.Ordinal);
            (input ? inputNames
                   : outputNames).Add(field.Name);
            outside.Add(input ? new(node.Id, "value", instanceId, field.Name)
                              : new(instanceId, field.Name, node.Id, "value"));
        }
        foreach (WireDefinition wire in original.Wires.Where(wire => !selected.Contains(wire.FromNode) && selected.Contains(wire.ToNode)))
        {
            NodeDefinition node = nodes.First(item => string.Equals(item.Id, wire.ToNode, StringComparison.Ordinal));
            string name = Input(node, wire.ToPort, types.Port(node.Id, wire.ToPort, false).DataType ?? types.OutputType(wire), distinct: string.Equals(wire.ToPort, Contracts.Targets.PcSelectionPorts.Execution, StringComparison.Ordinal));
            outside.Add(wire with { ToNode = instanceId, ToPort = name });
        }
        foreach (NodeDefinition node in nodes)
        {
            MissingInputs(node);
            Outputs(node);
        }
        WorkflowDefinition recipe = new("subworkflow-" + Guid.NewGuid().ToString("N")[..12], text["New SubWorkflow"], [.. copied, .. boundaries], inside.ToArray(), ContainsPersonalData: original.ContainsPersonalData) { IsRecipe = true };
        _ = SubworkflowInterface.Read(recipe);
        NodeDefinition instance = new(instanceId, SubworkflowInterface.CallType(recipe.Id), JsonSerializer.SerializeToElement(new { }), Position: new(left, top));
        WorkflowDefinition parent = original with
        {
            Nodes = [.. original.Nodes.Where(node => !selected.Contains(node.Id) || SubworkflowInterface.IsBoundary(node.Type)).Select(node => selected.Contains(node.Id) ? node with { Inputs = null }
                                                                                                                                                                          : node), instance],
            Wires = outside.ToArray(),
        };
        return new(original, parent, ContractJson.Copy(recipe), instanceId);
    }

    /// <summary>未設定の必須入力と、接続可能な必須設定を公開します。</summary>
    private void MissingInputs(NodeDefinition node)
    {
        NodeDescriptor descriptor = types.Descriptor(node.Id)!;
        foreach (PortDescriptor port in descriptor.Inputs.Where(port => port.Required && !port.IsLegacy))
        {
            if (!Supplied(node, port.Name) && (port.OverridesSetting is null || !node.Settings.TryGetProperty(port.OverridesSetting, out _)))
            {
                Input(node, port.Name, types.Port(node.Id, port.Name, false).DataType);
            }
        }
        string propertiesKey = "properties", requiredKey = "required";
        if (!descriptor.SettingsSchema.TryGetProperty(propertiesKey, out JsonElement properties))
        {
            return;
        }
        HashSet<string?> required = descriptor.SettingsSchema.TryGetProperty(requiredKey, out JsonElement list) ? list.EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal)
                                                                                                                : new(StringComparer.Ordinal);
        foreach (JsonProperty property in properties.EnumerateObject())
        {
            string port = SettingPorts.Name(property.Name);
            bool needed = required.Contains(property.Name) || NodeSettingVisibility.IsRequired(property.Value, node.Settings);
            if (needed && SettingPorts.CanConnect(property.Value) && NodeSettingVisibility.IsVisible(property.Value, node.Settings)
                && !Supplied(node, port) && !node.Settings.TryGetProperty(property.Name, out _)
                && !descriptor.Inputs.Any(input => string.Equals(input.OverridesSetting, property.Name, StringComparison.Ordinal) && Supplied(node, input.Name)))
            {
                Input(node, port, types.Port(node.Id, port, false).DataType);
            }
        }
    }

    /// <summary>未接続と既存の供給を区別します。</summary>
    /// <returns>定数または配線を持つ場合はtrueです。</returns>
    private bool Supplied(NodeDefinition node, string port) => node.Inputs?.ContainsKey(port) == true
        || original.Wires.Any(wire => string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal) && string.Equals(wire.ToPort, port, StringComparison.Ordinal));

    /// <summary>同じ入力端点は一つだけ公開します。</summary>
    /// <returns>配置ノードで使う公開名です。</returns>
    private string Input(NodeDefinition node, string port, ValueTypeDefinition? type, bool distinct = false)
    {
        if (!distinct && inputPorts.TryGetValue((node.Id, port), out string? existing))
        {
            return existing;
        }
        string name = Name(node, port, false, inputNames);
        NodeDefinition boundary = Boundary(false, name, type);
        boundaries.Add(boundary);
        inside.Add(new(boundary.Id, "value", node.Id, port));
        inputPorts[(node.Id, port)] = name;
        return name;
    }

    /// <summary>外向きの条件と抽出を内部の境界線へ移し、外側で二重適用しません。</summary>
    private void Outputs(NodeDefinition node)
    {
        WireDefinition[] outgoing = original.Wires.Where(wire => string.Equals(wire.FromNode, node.Id, StringComparison.Ordinal)).ToArray();
        foreach (IGrouping<(string Port, string When, string? Path), WireDefinition> group in outgoing.Where(wire => !selected.Contains(wire.ToNode)).GroupBy(wire => (wire.FromPort, wire.When, wire.Path)))
        {
            WireDefinition originalWire = group.First();
            string name = Output(node, group.Key.Port, group.Key.When, group.Key.Path, types.OutputType(originalWire));
            outside.AddRange(group.Select(wire => wire with { FromNode = instanceId, FromPort = name, When = "success", Path = null }));
        }
        foreach (PortDescriptor port in types.Descriptor(node.Id)!.Outputs.Where(port => !port.IsLegacy && !outgoing.Any(wire => string.Equals(wire.FromPort, port.Name, StringComparison.Ordinal))))
        {
            Output(node, port.Name, "success", null, types.Port(node.Id, port.Name, true).DataType);
        }
    }

    /// <summary>公開出力を一つの返却境界へ対応付けます。</summary>
    /// <returns>同方向で一意な公開名です。</returns>
    private string Output(NodeDefinition node, string port, string when, string? path, ValueTypeDefinition? type)
    {
        string name = Name(node, port, true, outputNames);
        NodeDefinition boundary = Boundary(true, name, type);
        boundaries.Add(boundary);
        inside.Add(new(node.Id, port, boundary.Id, "value", when, path));
        return name;
    }

    /// <summary>表示言語のノード名と引数名から公開名を作ります。</summary>
    /// <returns>長さと予約文字を調整し、同名には連番を付けます。</returns>
    private string Name(NodeDefinition node, string port, bool output, HashSet<string> used)
    {
        string label = SettingPorts.Property(port) is { } property ? text.SettingName(property)
                                                                   : text.PortName(node.Type, port, output);
        string basis = (text.NodeName(types.Descriptor(node.Id)) + " - " + label).Replace('/', '／');
        int length = Math.Min(110, basis.Length);
        if (length < basis.Length && char.IsHighSurrogate(basis[length - 1]))
        {
            length--;
        }
        basis = basis[..length];
        if (basis.StartsWith('$'))
        {
            basis = "＄" + basis[1..];
        }
        string name = basis;
        int suffix = 2;
        while (!used.Add(name))
        {
            name = basis + " (" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            suffix++;
        }
        return name;
    }

    /// <summary>型不明を文字列へ固定せず、実際の契約を持つ境界を配置します。</summary>
    /// <returns>通常の公開入出力ノードです。</returns>
    private NodeDefinition Boundary(bool output, string name, ValueTypeDefinition? type)
    {
        string boundaryType = SubworkflowInterface.InputType;
        NodePosition position = new(40, inputY);
        if (output)
        {
            boundaryType = SubworkflowInterface.OutputType;
            position = new(outputX, outputY);
        }
        NodeDescriptor? descriptor = catalog.FirstOrDefault(item => string.Equals(item.Type, boundaryType, StringComparison.Ordinal));
        double spacing = Math.Max(440, GraphPresentation.CardHeight(descriptor, 3, 3, true, new())) + 60;
        if (output)
        {
            outputY += spacing;
        }
        else
        {
            inputY += spacing;
        }
        return new("port-" + Guid.NewGuid().ToString("N")[..12], boundaryType,
            JsonSerializer.SerializeToElement(new { name, valueType = type ?? new("any"), hasDefault = false }, ContractJson.Options), Position: position);
    }

    /// <summary>反復や制限区間の開始と終了を切り離して、意味の異なるレシピへ変えません。</summary>
    private void ValidateLoops()
    {
        foreach (WireDefinition wire in original.Wires.Where(wire => types.Descriptor(wire.FromNode) is { } descriptor && WorkflowRegionNodeTypes.IsStart(descriptor.Type)
            && string.Equals(wire.FromPort, WorkflowRegionNodeTypes.PairPort(descriptor.Type), StringComparison.Ordinal)))
        {
            if (selected.Contains(wire.FromNode) != selected.Contains(wire.ToNode))
            {
                throw UiError.InvalidOperation("Select both execution boundaries when extracting a SubWorkflow.");
            }
        }
    }
}
