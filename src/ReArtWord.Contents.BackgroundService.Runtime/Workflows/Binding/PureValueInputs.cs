using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;

/// <summary>起動契機と、非起動の純粋値によるデータ供給を分けます。</summary>
internal static class PureValueInputs
{
    /// <summary>開始口が未到着の枝を除き、起動済みの処理に不足する入力を明示します。</summary>
    /// <returns>受信データを開始口へつなぐ既存配線も、非選択と入力不足に分けた結果です。</returns>
    internal static NodeResult MissingResult(NodeDefinition node, CompiledWorkflow workflow, IReadOnlyDictionary<string, NodeValue> inputs,
        IReadOnlyDictionary<string, ExecutionFrame> frames, bool entry, bool received, string peer = "")
    {
        string trigger = "trigger", value = "value";
        bool waiting = workflow.Incoming[node.Id].Any(wire => string.Equals(wire.ToPort, trigger, StringComparison.Ordinal)) && !inputs.ContainsKey(trigger);

        // 明示開始は入力不足を隠しません。暗黙開始では、宛先などが依存する非選択を共通データで迂回させません。
        if (!entry && !inputs.ContainsKey(trigger))
        {
            IReadOnlyList<WireDefinition> wires = workflow.Incoming[node.Id];
            foreach (WireDefinition wire in wires)
            {
                if ((!waiting || string.Equals(wire.ToPort, trigger, StringComparison.Ordinal)) && NeedsInput(wire, node, inputs)
                    && BranchInputSelection.IsExcluded(workflow, wire, frames[wire.FromNode], peer)
                    && wires.Where(candidate => string.Equals(candidate.ToPort, wire.ToPort, StringComparison.Ordinal))
                        .All(candidate => BranchInputSelection.IsExcluded(workflow, candidate, frames[candidate.FromNode], peer)))
                {
                    return NodeResult.Failure(ExecutionResultReasons.BranchNotSelected, "必須入力が依存する分岐が選択されなかったため、この処理は対象外です。", EOutcome.NotExecuted);
                }
            }
            if (!waiting && workflow.RecipeInputs.TryGetValue(node.Id, out IReadOnlyList<string>? recipeInputs)
                && recipeInputs.Any(id => frames.TryGetValue(id, out ExecutionFrame? frame)
                    && BranchInputSelection.IsExcluded(workflow, new(id, value, node.Id, value), frame, peer)))
            {
                return NodeResult.Failure(ExecutionResultReasons.BranchNotSelected, "レシピ入力が依存する分岐が選択されなかったため、この処理は対象外です。", EOutcome.NotExecuted);
            }
        }

        // 開始口が非選択なら他のデータ待ちでは起動しません。未到着の開始事象だけは待機を維持します。
        if (!entry && !inputs.ContainsKey(trigger) && workflow.Incoming[node.Id].Any(wire => (!waiting || string.Equals(wire.ToPort, trigger, StringComparison.Ordinal))
            && !inputs.ContainsKey(wire.ToPort) && frames[wire.FromNode].AwaitingEvent))
        {
            return NodeResult.Failure("input.eventPending", "合流事象の入力を待っています。この先行評価では処理を開始しません。", EOutcome.NotExecuted);
        }
        if (entry || received && !waiting)
        {
            return NodeResult.Failure("input.unavailable", "開始された経路の必須入力を取得できなかったため、この処理は実行していません。入力元の結果を確認してください。", EOutcome.NotExecuted);
        }
        return NodeResult.Failure("input.missing", "選択されなかった経路または未供給の必須入力があります。", EOutcome.NotExecuted);
    }

    /// <summary>実行先の利用可能性より先に、選択された経路か確認します。</summary>
    /// <returns>起動信号がない理由、または選択済みならnullです。</returns>
    internal static NodeResult? CheckActivation(NodeRegistry registry, NodeDefinition node, CompiledWorkflow workflow, Dictionary<string, ExecutionFrame> frames)
    {
        try
        {
            (IReadOnlyDictionary<string, NodeValue> inputs, bool received, bool missing) = Create(registry, node, string.Empty, workflow, frames, null);
            if (!received || missing)
            {
                NodeResult reason = MissingResult(node, workflow, inputs, frames, false, received);
                if (!received || string.Equals(reason.ReasonCode, ExecutionResultReasons.BranchNotSelected, StringComparison.Ordinal))
                {
                    return reason;
                }
            }
            return null;
        }
        catch (FormatException)
        {
            return NodeResult.Failure("input.path", "入力の型と抽出パスが一致しません。", EOutcome.NotExecuted);
        }
    }

    /// <summary>通常の入力合成を共用し、必要なpure値だけを補います。</summary>
    /// <returns>合成入力、実経路からの起動信号、明示pure接続の未入力です。</returns>
    internal static (IReadOnlyDictionary<string, NodeValue> Inputs, bool Received, bool Missing) Create(NodeRegistry registry, NodeDefinition node, string peer,
        CompiledWorkflow workflow, Dictionary<string, ExecutionFrame> frames, IReadOnlyDictionary<string, NodeValue>? initial, bool requireConnected = true, bool ignoreTrigger = false,
        IReadOnlyList<WireDefinition>? incoming = null)
    {
        string trigger = "trigger";
        IReadOnlyList<WireDefinition> wires = incoming ?? workflow.Incoming[node.Id];
        if (ignoreTrigger)
        {
            wires = wires.Where(wire => !string.Equals(wire.ToPort, trigger, StringComparison.Ordinal)).ToArray();
        }
        IReadOnlyDictionary<string, NodeResult>? memo = workflow.ConfigurationValues;
        NodeDescriptor? descriptor = registry.Find(node.Type)?.Descriptor;
        IReadOnlyDictionary<string, NodeValue> active = WireInputs.Create(node, peer, wires, frames, initial, descriptor: descriptor);
        (bool scopeMissing, bool scopeReceived) = RecipeIterationInputs.Check(workflow, node.Id, frames.Values);

        // StartSignalの明示配線は独立したデータ入力で迂回させません。既存の通知経路の合流規則は維持します。
        WireDefinition[] triggers = wires.Where(wire => string.Equals(wire.ToPort, trigger, StringComparison.Ordinal)
            && StartSignalPorts.IsOutput(registry.Find(workflow.FindNode(wire.FromNode)!.Type)?.Descriptor, wire.FromPort)).ToArray();
        WireDefinition[] arriving = triggers.Where(wire => !frames[wire.FromNode].ConfigurationOnly && !frames[wire.FromNode].RetainedValue).ToArray();
        bool missingTrigger = triggers.Length != 0 && !WireInputs.Create(node with { Inputs = null }, peer, arriving, frames, null).ContainsKey(trigger);
        bool received = scopeReceived || wires.Any(wire => !frames[wire.FromNode].ConfigurationOnly && active.ContainsKey(wire.ToPort));
        received &= !missingTrigger && !scopeMissing;
        if (memo is null || !frames.Values.Any(frame => frame.ConfigurationOnly))
        {
            return (active, received, scopeMissing || missingTrigger || wires.Any(wire => requireConnected && Missing(wire, node, frames[wire.FromNode], active)));
        }
        Dictionary<string, ExecutionFrame> supplied = new(frames, StringComparer.Ordinal);
        foreach (ExecutionFrame frame in frames.Values.Where(frame => frame.ConfigurationOnly))
        {
            if (memo.TryGetValue(frame.NodeId, out NodeResult? value) && value.Outcome == EOutcome.Succeeded)
            {
                supplied[frame.NodeId] = new(frame.NodeId, value.Outcome, [new("configuration", value)]);
            }
        }
        string result = "result";
        WireDefinition[] valueWires = wires.Where(wire => !frames[wire.FromNode].ConfigurationOnly || frames[wire.FromNode].RetainedValue
            || !string.Equals(wire.FromPort, result, StringComparison.Ordinal) && !CompletionPorts.IsPort(wire.FromPort)).ToArray();
        IReadOnlyDictionary<string, NodeValue> inputs = WireInputs.Create(node, peer, valueWires, supplied, initial, descriptor: descriptor);
        bool missing = scopeMissing || missingTrigger || wires.Any(wire => (requireConnected || frames[wire.FromNode].ConfigurationOnly) && Missing(wire, node, frames[wire.FromNode], inputs));
        return (inputs, received, missing);
    }

    /// <summary>未供給のデータと、選択されなかった明示結果経路・待受ライフサイクルを区別します。</summary>
    /// <returns>この一件で供給すべき接続値が欠けているかです。</returns>
    private static bool Missing(WireDefinition wire, NodeDefinition node, ExecutionFrame frame, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        if (!NeedsInput(wire, node, inputs))
        {
            return false;
        }
        string success = "success", any = "*";
        if (string.Equals(wire.When, success, StringComparison.Ordinal) && (frame.SourceActivation || frame.SourceCompletion))
        {
            return false;
        }
        return string.Equals(wire.When, success, StringComparison.Ordinal) || string.Equals(wire.When, any, StringComparison.Ordinal)
            || frame.Targets.Any(target => string.Equals(target.Result.Route, wire.When, StringComparison.Ordinal));
    }

    /// <summary>合流候補や未使用デフォルトを除き、この接続の値を必要としているか確認します。</summary>
    /// <returns>供給済みでも任意候補でもない入力です。</returns>
    private static bool NeedsInput(WireDefinition wire, NodeDefinition node, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        if (Contracts.Values.DefaultIfNullValues.IsUnusedFallback(node.Type, wire.ToPort, inputs))
        {
            return false;
        }
        bool candidate = node.Type is "control.toggleButton" && wire.ToPort is "offValue" or "onValue";
        bool iterationControl = IterationNodeTypes.IsEnd(node.Type) && (IterationNodeTypes.IsControlPort(wire.ToPort)
            || 0 < ValuePortPairs.Index(wire.ToPort) && IterationNodeTypes.RequestedControl(inputs) is not null);
        if (inputs.ContainsKey(wire.ToPort) || EventMergePorts.IsNode(node.Type) && 0 < ConcatPorts.Index(wire.ToPort)
            || candidate || iterationControl)
        {
            return false;
        }
        return true;
    }
}
