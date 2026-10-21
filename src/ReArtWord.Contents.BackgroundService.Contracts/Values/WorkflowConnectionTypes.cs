using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>編集と実行前検証で共有する保守的な接続型検査です。</summary>
public static class WorkflowConnectionTypes
{
    /// <summary>置換後に要素型が判明して既存の後続接続を不適合にする変更も拒否します。</summary>
    /// <returns>対象入力の旧配線を置き換えても、既知の型違反を追加しないかです。</returns>
    public static bool CanReplaceConnection(WorkflowDefinition document, IReadOnlyList<NodeDescriptor> catalog, WireDefinition wire)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(wire);
        NodeDefinition? destination = document.Nodes.FirstOrDefault(node => string.Equals(node.Id, wire.ToNode, StringComparison.Ordinal));
        NodeDescriptor? destinationType = catalog.FirstOrDefault(node => string.Equals(node.Type, destination?.Type, StringComparison.Ordinal));
        bool multiple = PcSelectionPorts.AllowsMultiple(destinationType, wire.ToPort);
        WorkflowDefinition proposed = document with
        {
            Wires = [.. document.Wires.Where(current => multiple || !string.Equals(current.ToNode, wire.ToNode, StringComparison.Ordinal)
                || !string.Equals(current.ToPort, wire.ToPort, StringComparison.Ordinal)), wire],
        };
        WorkflowConnectionInspection next = new(proposed, catalog);
        WorkflowConnectionInspection currentInspection = new(document, catalog);
        if (!CanConnect(next, wire))
        {
            return false;
        }
        HashSet<string> visited = new(StringComparer.Ordinal);
        Queue<string> pending = new();
        pending.Enqueue(wire.ToNode);
        ILookup<string, WireDefinition> outgoing = proposed.Wires.ToLookup(current => current.FromNode, StringComparer.Ordinal);
        while (pending.TryDequeue(out string? node))
        {
            if (string.Equals(node, wire.FromNode, StringComparison.Ordinal))
            {
                return false;
            }
            if (!visited.Add(node))
            {
                continue;
            }
            foreach (WireDefinition downstream in outgoing[node])
            {
                if (!CanConnect(next, downstream) && CanConnect(currentInspection, downstream))
                {
                    return false;
                }
                pending.Enqueue(downstream.ToNode);
            }
        }
        return true;
    }

    /// <summary>旧配線の推測型でANY入力を狭めず、判明している不適合だけを拒否します。</summary>
    /// <returns>型契約上接続を許可できるかです。実行時の検証は別途必要です。</returns>
    public static bool CanConnect(WorkflowDefinition document, IReadOnlyList<NodeDescriptor> catalog, WireDefinition wire)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(wire);
        return CanConnect(new WorkflowConnectionInspection(document, catalog), wire);
    }

    /// <summary>同じ保存版の接続群を検査し、グラフ全体の索引を再生成しません。</summary>
    /// <returns>単独検査と同じ接続可否です。</returns>
    public static bool CanConnect(WorkflowConnectionInspection inspection, WireDefinition wire)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(wire);
        WorkflowDefinition document = inspection.Document;
        NodeDefinition? source = inspection.FindNode(wire.FromNode);
        NodeDefinition? target = inspection.FindNode(wire.ToNode);
        if (source is null || target is null || string.Equals(source.Id, target.Id, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(wire.FromPort) || string.IsNullOrWhiteSpace(wire.ToPort)
            || (wire.Path is not null && !JsonPointer.IsValid(wire.Path)))
        {
            return false;
        }
        bool loopOutput = WorkflowRegionNodeTypes.IsStart(source.Type) && string.Equals(wire.FromPort, WorkflowRegionNodeTypes.PairPort(source.Type), StringComparison.Ordinal);
        bool loopInput = WorkflowRegionNodeTypes.IsEnd(target.Type) && string.Equals(wire.ToPort, WorkflowRegionNodeTypes.PairPort(target.Type), StringComparison.Ordinal);
        if (loopOutput || loopInput)
        {
            return loopOutput && loopInput && WorkflowRegionNodeTypes.IsPair(source.Type, target.Type) && wire.Path is null;
        }
        WorkflowSettingValues settings = inspection.Settings;
        NodeDescriptor? from = inspection.GetPorts(source.Id);
        NodeDescriptor? to = inspection.GetPorts(target.Id);
        if (from?.IsTerminal == true)
        {
            return false;
        }
        PortDescriptor? selectedOutput = from?.Outputs.FirstOrDefault(port => string.Equals(port.Name, wire.FromPort, StringComparison.Ordinal));
        bool mergeTrigger = EventMergePorts.IsNode(target.Type) && wire.ToPort is "trigger";
        if (mergeTrigger)
        {
            return false;
        }
        if (SignalMergePorts.IsNode(target.Type) && 0 < SignalMergePorts.Index(wire.ToPort)
            || IterationNodeTypes.IsEnd(target.Type) && IterationNodeTypes.IsControlPort(wire.ToPort))
        {
            return StartSignalPorts.IsOutput(from, wire.FromPort) && wire.Path is null && wire.When is "success" or "*";
        }
        WorkflowTypeInference inference = inspection.Inference;
        ValueTypeDefinition? inferred = inference.OutputType(wire with { Path = null });
        bool selection = selectedOutput?.ValueKind == EPortValueKind.PcSelection || string.Equals(inferred?.Type, PcSelectionPorts.TypeName, StringComparison.Ordinal);
        if (ValueMergePorts.IsNode(target.Type) && 0 < ConcatPorts.Index(wire.ToPort)
            && document.Wires.Where(item => string.Equals(item.ToNode, target.Id, StringComparison.Ordinal) && !string.Equals(item.ToPort, wire.ToPort, StringComparison.Ordinal) && 0 < ConcatPorts.Index(item.ToPort))
                .Any(item => selection != string.Equals(inference.OutputType(item)?.Type, PcSelectionPorts.TypeName, StringComparison.Ordinal)))
        {
            return false;
        }
        if (selection || PcSelectionPorts.AllowsMultiple(to, wire.ToPort))
        {
            bool execution = string.Equals(wire.ToPort, PcSelectionPorts.Execution, StringComparison.Ordinal);
            return selection && (PcSelectionPorts.AllowsMultiple(to, wire.ToPort) || PcSelectionPorts.IsRelay(target, wire.ToPort))
                && (!execution || (to?.CanEvaluateStatically != true && target.Target is null)) && wire.Path is null && wire.When is "success" or "*";
        }
        string trigger = "trigger";
        string result = "result";
        string sourcePort = "source";
        bool commonOutput = string.Equals(wire.FromPort, result, StringComparison.Ordinal)
            || (from?.Kind == ENodeKind.Source && string.Equals(wire.FromPort, sourcePort, StringComparison.Ordinal));
        PortDescriptor? output = from?.Outputs.FirstOrDefault(item => string.Equals(item.Name, wire.FromPort, StringComparison.Ordinal));
        if (from is not null && output is null && !commonOutput)
        {
            return false;
        }
        EPortValueKind kind = output?.ValueKind ?? EPortValueKind.Any;
        if (commonOutput || (IterationNodeTypes.IsStart(source.Type) && IterationNodeTypes.IsElementPort(wire.FromPort)))
        {
            kind = EPortValueKind.Json;
        }
        if (kind == EPortValueKind.Any && inferred is not null)
        {
            string binary = "binary";
            kind = EPortValueKind.Json;
            if (string.Equals(inferred.Type, binary, StringComparison.Ordinal))
            {
                kind = EPortValueKind.Binary;
            }
        }
        if (!string.IsNullOrEmpty(wire.Path))
        {
            if (kind == EPortValueKind.Binary)
            {
                return false;
            }
            inferred = inference.OutputType(wire);
            kind = EPortValueKind.Json;
        }
        if (string.Equals(wire.ToPort, trigger, StringComparison.Ordinal))
        {
            return true;
        }
        string? property = SettingPorts.Property(wire.ToPort);
        if (property is not null)
        {
            if (kind == EPortValueKind.Binary || (to is not null && !SettingPorts.IsDeclared(to, wire.ToPort))
                || (!(to is not null && SettingPorts.AllowsRuntimeValue(to, wire.ToPort)) && !CanSupplySetting(inspection, wire)))
            {
                return false;
            }
            string argumentTypes = "argumentTypes";
            if (OscPorts.Supports(target.Type) && string.Equals(property, argumentTypes, StringComparison.Ordinal)
                && settings.Setting(target, property) is { } types && OscPorts.ArgumentTypes(types.Json) is null)
            {
                return false;
            }
            return to is null || MatchesSetting(to.SettingsSchema, property, inferred);
        }
        PortDescriptor? input = to?.Inputs.FirstOrDefault(item => string.Equals(item.Name, wire.ToPort, StringComparison.Ordinal));
        if (string.Equals(target.Type, ConcatPorts.NodeType, StringComparison.Ordinal) && 0 < ConcatPorts.Index(wire.ToPort))
        {
            try
            {
                _ = ConcatValues.ResultType(Enumerable.Range(1, ConcatPorts.Maximum).Select(ConcatPorts.Name)
                    .Where(port => !string.Equals(port, wire.ToPort, StringComparison.Ordinal)).Select(port => inference.PortType(target.Id, port, false)).Append(inferred));
            }
            catch (FormatException)
            {
                return false;
            }
        }
        string otherPort = "left";
        if (string.Equals(wire.ToPort, otherPort, StringComparison.Ordinal))
        {
            otherPort = "right";
        }
        return (to is null || input is not null) && (input is null || input.ValueKind == EPortValueKind.Any || kind == EPortValueKind.Any || input.ValueKind == kind)
            && ValueTypeInference.CanAssign(input?.DataType, inferred)
            && BuiltinInputTypes.Accepts(target.Type, wire.ToPort, inferred, inference.PortType(target.Id, otherPort, false))
            && MatchesArrayElement(target, wire.ToPort, inferred, inference)
            && MatchesTypeDefinition(target, wire.ToPort, settings)
            && ValueTypeInference.CanAssign(Scripting.ScriptSettings.PortType(target, wire.ToPort, false), inferred)
            && ValueTypeInference.CanAssign(OscPorts.PortType(target, wire.ToPort), inferred);
    }

    /// <summary>値が確認できる型入力では、単なるJSONオブジェクトを型定義と取り違えません。</summary>
    /// <returns>既知の構造が型契約に適合するかです。</returns>
    private static bool MatchesTypeDefinition(NodeDefinition node, string port, WorkflowSettingValues settings)
    {
        string typeNode = "value.type";
        string properties = "properties";
        if (string.Equals(node.Type, typeNode, StringComparison.Ordinal) && string.Equals(port, properties, StringComparison.Ordinal))
        {
            NodeValue? mapping = settings.Input(node, port);
            return mapping is null || ValueTypeInference.Properties(mapping) is not null;
        }
        string definition = "type definition";
        if (!string.Equals(BuiltinInputTypes.Expected(node.Type, port)?.Type, definition, StringComparison.Ordinal))
        {
            return true;
        }
        NodeValue? value = settings.Input(node, port);
        return value is null || ValueTypeInference.Descriptor(value) is not null;
    }

    /// <summary>配列builderの明示要素型を、単体入力と既存配列の両方へ適用します。</summary>
    /// <returns>既知の要素型が契約に適合するかです。</returns>
    private static bool MatchesArrayElement(NodeDefinition target, string port, ValueTypeDefinition? actual, WorkflowTypeInference inference)
    {
        string array = "value.array";
        string item = "item";
        string items = "items";
        if (!string.Equals(target.Type, array, StringComparison.Ordinal) || (!string.Equals(port, item, StringComparison.Ordinal) && !string.Equals(port, items, StringComparison.Ordinal)))
        {
            return true;
        }
        if (string.Equals(port, items, StringComparison.Ordinal))
        {
            actual = actual?.ElementType;
        }
        return ValueTypeInference.CanAssign(inference.ArrayElementType(target.Id), actual);
    }

    /// <summary>通常処理の結果や動的入力を設定値として接続しません。</summary>
    /// <returns>既知の非静的依存がないかです。</returns>
    private static bool CanSupplySetting(WorkflowConnectionInspection inspection, WireDefinition wire)
    {
        Queue<WireDefinition> pending = new();
        HashSet<string> visited = new(StringComparer.Ordinal);
        ILookup<string, WireDefinition> inputs = inspection.Incoming;
        pending.Enqueue(wire);
        string result = "result";
        string success = "success";
        string anyRoute = "*";
        while (pending.TryDequeue(out WireDefinition? current))
        {
            if ((!string.Equals(current.When, success, StringComparison.Ordinal) && !string.Equals(current.When, anyRoute, StringComparison.Ordinal)) || string.Equals(current.FromPort, result, StringComparison.Ordinal) || CompletionPorts.IsPort(current.FromPort))
            {
                return false;
            }
            if (!visited.Add(current.FromNode))
            {
                continue;
            }
            NodeDefinition? node = inspection.FindNode(current.FromNode);
            if (node is not null && SubworkflowInterface.IsSubworkflow(inspection.Document) && string.Equals(node.Type, SubworkflowInterface.InputType, StringComparison.Ordinal))
            {
                continue;
            }
            if (node is null || inspection.FindDescriptor(node.Type)?.CanEvaluateStatically == false)
            {
                return false;
            }
            foreach (WireDefinition input in inputs[current.FromNode])
            {
                pending.Enqueue(input);
            }
        }
        return true;
    }

    /// <summary>設定スキーマのJSON型のみを照合し、値や範囲を架空に評価しません。</summary>
    /// <returns>不適合が確定しない場合はtrueです。</returns>
    private static bool MatchesSetting(JsonElement schema, string property, ValueTypeDefinition? inferred)
    {
        string propertiesKey = "properties";
        string typeKey = "type";
        if (inferred is null || inferred.Optional || schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty(propertiesKey, out JsonElement properties)
            || !properties.TryGetProperty(property, out JsonElement setting) || setting.ValueKind != JsonValueKind.Object
            || !setting.TryGetProperty(typeKey, out JsonElement declared))
        {
            return true;
        }
        string actual = inferred.Type switch
        {
            "byte" or "int64" => "integer",
            "decimal" => "number",
            "char" or "path" => "string",
            "type definition" => "object",
            _ => inferred.Type,
        };
        string any = "any";
        if (string.Equals(actual, any, StringComparison.Ordinal))
        {
            return true;
        }
        if (declared.ValueKind == JsonValueKind.Array)
        {
            return declared.EnumerateArray().Any(item => CompatibleType(item, actual));
        }
        return CompatibleType(declared, actual);
    }

    /// <summary>整数と数値の包含関係は許可し、整数性未確定の数値もここでは拒否しません。</summary>
    /// <returns>確認可能な型互換性です。</returns>
    private static bool CompatibleType(JsonElement declared, string actual)
    {
        if (declared.ValueKind != JsonValueKind.String)
        {
            return true;
        }
        string? expected = declared.GetString();
        string number = "number";
        string integer = "integer";
        return string.Equals(expected, actual, StringComparison.Ordinal)
            || ((string.Equals(expected, number, StringComparison.Ordinal) || string.Equals(expected, integer, StringComparison.Ordinal))
                && (string.Equals(actual, number, StringComparison.Ordinal) || string.Equals(actual, integer, StringComparison.Ordinal)));
    }
}
