using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Targets;

/// <summary>先着したPC候補を一回の入力から取り出し、固定した参加情報で実行先に変換します。</summary>
internal static class RuntimePcSelection
{
    /// <summary>別の実行や過去の保持値を見ず、対応する親フレームだけを使用します。</summary>
    /// <returns>選択した実体または未実行理由です。</returns>
    internal static TargetResolution Resolve(NodeDefinition node, CompiledWorkflow workflow, NodeRegistry registry,
        Dictionary<string, ExecutionFrame> frames, PcSelectionResolver resolver)
    {
        try
        {
            WireDefinition[] candidates = workflow.Incoming[node.Id].Where(wire => string.Equals(wire.ToPort, PcSelectionPorts.Execution, StringComparison.Ordinal)).ToArray();
            IReadOnlyDictionary<string, NodeValue> inputs = PureValueInputs.Create(registry, node, string.Empty, workflow, frames, null, incoming: candidates).Inputs;
            if (inputs.TryGetValue(PcSelectionPorts.Execution, out NodeValue? value))
            {
                return resolver.Resolve(PcSelectionValue.Read(value), node.PcExecution);
            }
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or ArgumentException)
        {
            return new(EExecutionScope.One, [], NodeResult.Failure("selection.inputInvalid", "この実行で受け取ったPC候補が不正です。", EOutcome.NotExecuted));
        }
        return new(EExecutionScope.One, [], NodeResult.Failure("selection.inputUnavailable", "この実行のPC候補が届いていません。", EOutcome.NotExecuted));
    }
}
