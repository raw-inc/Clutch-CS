using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;

/// <summary>直前の結果だけから、未供給の入力が確定した分岐非選択によるものかを判断します。</summary>
internal static class BranchInputSelection
{
    /// <summary>どの候補も非選択と確定した合流だけを閉じ、未到着の候補は待機のまま残します。</summary>
    /// <returns>合流の非選択結果、または他の状態を維持する場合はnullです。</returns>
    internal static ExecutionFrame? ClosedMerge(CompiledWorkflow workflow, string node, ExecutionFrame[] parents)
    {
        WireDefinition[] candidates = workflow.Incoming[node].Where(wire => 0 < ConcatPorts.Index(wire.ToPort)).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }
        Dictionary<string, ExecutionFrame> frames = parents.ToDictionary(frame => frame.NodeId, StringComparer.Ordinal);
        if (!candidates.All(wire => frames.TryGetValue(wire.FromNode, out ExecutionFrame? frame) && IsExcluded(workflow, wire, frame, string.Empty)))
        {
            return null;
        }
        return new(node, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure(ExecutionResultReasons.BranchNotSelected,
            "すべての合流候補が依存する分岐が選択されなかったため、この合流は対象外です。", EOutcome.NotExecuted))]);
    }

    /// <summary>途中の計算が引き継いだ非選択と、条件ノードの非選択側の出力を識別します。</summary>
    /// <returns>待機・障害・出力欠損を含まず、その接続が非選択と確認できる場合です。</returns>
    internal static bool IsExcluded(CompiledWorkflow workflow, WireDefinition wire, ExecutionFrame frame, string peer)
    {
        if (frame.AwaitingEvent || frame.ConfigurationOnly || frame.RetainedValue || frame.SourceActivation || frame.SourceCompletion)
        {
            return false;
        }
        IReadOnlyList<TargetNodeResult> targets = WireInputs.TargetsFor(frame, peer);
        if (targets.Count == 0)
        {
            return false;
        }
        string success = "success", any = "*";
        if (string.Equals(wire.When, success, StringComparison.Ordinal) && frame.Outcome == EOutcome.NotExecuted)
        {
            return targets.All(target => target.Result.Outcome == EOutcome.NotExecuted
                && (target.Result.ReasonCode is "condition.notMatched" || string.Equals(target.Result.ReasonCode, ExecutionResultReasons.BranchNotSelected, StringComparison.Ordinal)));
        }
        if (frame.Outcome != EOutcome.Succeeded || targets.Any(target => target.Result.Outcome != EOutcome.Succeeded))
        {
            return false;
        }
        if (!string.Equals(wire.When, success, StringComparison.Ordinal) && !string.Equals(wire.When, any, StringComparison.Ordinal))
        {
            return targets.All(target => !string.Equals(target.Result.Route, wire.When, StringComparison.Ordinal));
        }
        string? selectedPort = wire.FromPort switch { "trueStart" => "falseStart", "falseStart" => "trueStart", _ => null };
        return selectedPort is not null && workflow.FindNode(wire.FromNode)?.Type is "control.if"
            && targets.All(target => target.Result.Outputs.ContainsKey(selectedPort) && !target.Result.Outputs.ContainsKey(wire.FromPort));
    }
}
