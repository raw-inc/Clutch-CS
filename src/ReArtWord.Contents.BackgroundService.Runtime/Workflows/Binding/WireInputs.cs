using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;

/// <summary>同じ一件の結果だけから実行先に対応した入力を構築します。</summary>
internal static class WireInputs
{
    private static string SuccessRoute => "success";
    private static string AnyRoute => "*";
    private static string ResultPort => "result";
    private static string FileTransactionPort => "fileTransaction";

    /// <summary>同じPeerの結果を優先し、別Peerの複数結果は明示的な集合にします。</summary>
    /// <returns>渡されたポートだけを含む入力です。</returns>
    internal static IReadOnlyDictionary<string, NodeValue> Create(
        NodeDefinition node,
        string peerId,
        IReadOnlyList<WireDefinition> wires,
        IReadOnlyDictionary<string, ExecutionFrame> completed,
        IReadOnlyDictionary<string, NodeValue>? entryInputs,
        bool requireExistingPaths = false, NodeDescriptor? descriptor = null)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal);
        if (node.Inputs is not null)
        {
            foreach (KeyValuePair<string, NodeValue> item in node.Inputs)
            {
                inputs.Add(item.Key, item.Value.Copy());
            }
        }
        if (entryInputs is not null)
        {
            foreach (KeyValuePair<string, NodeValue> item in entryInputs)
            {
                inputs[item.Key] = item.Value.Copy();
            }
        }
        foreach (WireDefinition wire in wires)
        {
            ExecutionFrame frame = completed[wire.FromNode];
            bool iterationSignal = IterationNodeTypes.IsEnd(node.Type) && IterationNodeTypes.IsControlPort(wire.ToPort);
            if (iterationSignal && (frame.ConfigurationOnly || frame.RetainedValue))
            {
                inputs.Remove(wire.ToPort);
                continue;
            }
            bool setting = SettingsBinding.IsPort(wire.ToPort);
            if (setting)
            {
                inputs.Remove(wire.ToPort);
            }
            NodeValue? value;
            if (iterationSignal)
            {
                value = ReadIterationSignal(frame, wire, requireExistingPaths);
            }
            else
            {
                bool optional = setting && frame.Outcome == EOutcome.Succeeded && descriptor is not null
                    && OptionalSettingValues.CanOmit(descriptor.SettingsSchema, SettingPorts.Property(wire.ToPort));
                value = Read(frame, wire, peerId, (requireExistingPaths || setting) && !optional);
            }
            if (value is not null)
            {
                if (MergesPcSelection(node, wire.ToPort) && inputs.TryGetValue(wire.ToPort, out NodeValue? previous))
                {
                    value = NodeValue.FromObject(PcSelectionValue.Read(previous).Concat(PcSelectionValue.Read(value)).ToArray());
                }
                inputs[wire.ToPort] = value;
            }
        }
        string toggle = "control.toggleButton";
        if (string.Equals(node.Type, toggle, StringComparison.Ordinal))
        {
            // 未配線の任意値だけをnullにします。配線済みの未到着値は、選択後も未供給として扱います。
            foreach (string port in new[] { "offValue", "onValue" })
            {
                if (!wires.Any(wire => string.Equals(wire.ToPort, port, StringComparison.Ordinal)))
                {
                    inputs.TryAdd(port, NodeValue.Null);
                }
            }
        }
        return inputs;
    }

    /// <summary>PC選択型の公開境界でも複数条件を連結します。</summary>
    /// <returns>通常値を上書きする既存挙動とは区別したPC選択かです。</returns>
    private static bool MergesPcSelection(NodeDefinition node, string port)
    {
        if (string.Equals(port, PcSelectionPorts.Execution, StringComparison.Ordinal)
            || string.Equals(node.Type, PcSelectionPorts.LowestNode, StringComparison.Ordinal) && string.Equals(port, PcSelectionPorts.Candidates, StringComparison.Ordinal))
        {
            return true;
        }
        string value = "value";
        return (SubworkflowInterface.IsBoundary(node.Type) || string.Equals(node.Type, SubworkflowInterface.PortType, StringComparison.Ordinal))
            && string.Equals(port, value, StringComparison.Ordinal) && string.Equals(SubworkflowInterface.ReadField(node).Type.Type, PcSelectionPorts.TypeName, StringComparison.Ordinal);
    }

    /// <summary>PC別データの集合を作らず、同じ要素で確認できた真の制御信号を一つにします。</summary>
    /// <returns>一つでも届いた真の信号、または到着した非真値です。</returns>
    private static NodeValue? ReadIterationSignal(ExecutionFrame frame, WireDefinition wire, bool requireExistingPaths)
    {
        NodeValue? received = null;
        foreach (TargetNodeResult target in frame.Targets)
        {
            NodeValue? value = Read(frame with { Targets = [target] }, wire, target.PeerId, requireExistingPaths);
            if (value?.Kind == EValueKind.Json && value.Json.ValueKind == JsonValueKind.True)
            {
                return value;
            }
            received = value ?? received;
        }
        return received;
    }

    /// <summary>必要な結果経路が渡す値だけを読み取ります。</summary>
    /// <returns>未入力の場合はnull、正常なnullはNodeValueとして返します。</returns>
    private static NodeValue? Read(ExecutionFrame frame, WireDefinition wire, string peerId, bool requireExistingPaths)
    {
        if (string.Equals(wire.FromPort, CompletionPorts.Signal, StringComparison.Ordinal) || string.Equals(wire.FromPort, CompletionPorts.Success, StringComparison.Ordinal))
        {
            if (frame.SourceActivation || frame.SourceCompletion || frame.ElementStart || frame.ConfigurationOnly && !frame.RetainedValue)
            {
                return null;
            }
            if (string.Equals(wire.FromPort, CompletionPorts.Signal, StringComparison.Ordinal))
            {
                return !frame.RetainedValue && frame.Outcome is EOutcome.Succeeded or EOutcome.Failed or EOutcome.Cancelled ? NodeValue.FromObject(true)
                                                                                                                            : null;
            }
            return CompletionPorts.Status(frame.Outcome) is { } status ? Extract(NodeValue.FromObject(status), wire.Path, requireExistingPaths)
                                                                       : null;
        }
        if ((frame.SourceActivation || frame.SourceCompletion) && string.Equals(wire.When, SuccessRoute, StringComparison.Ordinal))
        {
            return null;
        }
        if (string.Equals(wire.When, SuccessRoute, StringComparison.Ordinal) && frame.Outcome != EOutcome.Succeeded)
        {
            return null;
        }
        if (frame.FileTransaction is not null)
        {
            NodeResult transactionOutcome = NodeResult.Failure("file.transaction", "ファイル全対象の段階結果です。", frame.Outcome);
            if (frame.Outcome == EOutcome.Succeeded)
            {
                transactionOutcome = NodeResult.Success();
            }
            if (!RouteMatches(wire.When, transactionOutcome))
            {
                return null;
            }
            if (string.Equals(wire.FromPort, FileTransactionPort, StringComparison.Ordinal))
            {
                return Extract(NodeValue.FromObject(frame.FileTransaction), wire.Path, requireExistingPaths);
            }
        }
        IReadOnlyList<TargetNodeResult> matches = TargetsFor(frame, peerId);
        List<(string PeerId, NodeValue Value)> values = [];
        foreach (TargetNodeResult target in matches)
        {
            if (frame.FileTransaction is null && !RouteMatches(wire.When, target.Result))
            {
                continue;
            }
            NodeValue? value = null;
            if (string.Equals(wire.FromPort, ResultPort, StringComparison.Ordinal))
            {
                value = NodeValue.FromObject(target);
            }
            else
            {
                if (PropertyPorts.TryRead(target.Result.Outputs, wire.FromPort, out NodeValue? output, requireExistingPaths))
                {
                    value = output!.Copy();
                }
            }
            if (value is not null)
            {
                NodeValue? selected = Extract(value, wire.Path, requireExistingPaths);
                if (selected is not null)
                {
                    values.Add((target.PeerId, selected));
                }
            }
        }
        if (values.Count == 0)
        {
            return null;
        }
        if (matches.Count == 1)
        {
            return values[0].Value;
        }
        return NodeValue.FromObject(values.Select(static item => new { peerId = item.PeerId, value = item.Value }));
    }

    /// <summary>入力値と未供給理由に同じ対象選択を適用します。</summary>
    /// <returns>同じPeerの結果、存在しない場合は全対象の結果です。</returns>
    internal static IReadOnlyList<TargetNodeResult> TargetsFor(ExecutionFrame frame, string peerId)
    {
        if (frame.Targets.Count <= 1)
        {
            return frame.Targets;
        }
        TargetNodeResult[] matches = frame.Targets.Where(target => string.Equals(target.PeerId, peerId, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
        {
            return frame.Targets;
        }
        return matches;
    }

    /// <summary>正常分岐と共通非成功の明示経路を区別します。</summary>
    /// <returns>この経路に配線された結果かを示します。</returns>
    private static bool RouteMatches(string when, NodeResult result)
    {
        if (string.Equals(when, AnyRoute, StringComparison.Ordinal))
        {
            return true;
        }
        if (string.Equals(when, SuccessRoute, StringComparison.Ordinal))
        {
            return result.Outcome == EOutcome.Succeeded;
        }
        return string.Equals(when, result.Route, StringComparison.Ordinal);
    }

    /// <summary>JSON Pointerの未存在を正常nullとして抽出します。</summary>
    /// <returns>抽出された値です。</returns>
    private static NodeValue? Extract(NodeValue value, string? path, bool requireExistingPaths)
    {
        if (path is null)
        {
            return value.Copy();
        }
        if (requireExistingPaths)
        {
            JsonPointer.TrySelect(value, path, out NodeValue? selected);
            return selected;
        }
        return JsonPointer.Select(value, path);
    }
}
