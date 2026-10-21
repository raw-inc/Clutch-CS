using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;

/// <summary>各要素の結果を通常の実行境界で受け、親エンジンに集約を委ねます。</summary>
public sealed class ForEachEndNode : IActionNode
{
    /// <summary>loopは対応付け専用とし、可変のJSON結果と明示制御信号を受け取ります。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe(IterationNodeTypes.End, "ForEach End", "Control", DictionaryValues.SettingsSchema,
        [new(IterationNodeTypes.LoopPort, EPortValueKind.Json, Required: true),
         .. Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ValuePortPairs.Name(index), EPortValueKind.Json)),
         new(IterationNodeTypes.ContinuePort, EPortValueKind.Json) { DataType = new("boolean", Optional: true) },
         new(IterationNodeTypes.BreakPort, EPortValueKind.Json) { DataType = new("boolean", Optional: true) }],
        Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ValuePortPairs.Name(index), EPortValueKind.Json)).ToArray()) with { HasAsyncCompletion = true };

    /// <summary>一つの収集先を指定します。</summary>
    /// <returns>設定と対象の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        if (definition.Target is not null && definition.Target.Scope != EExecutionScope.One)
        {
            issues.Add(new("iteration.target", "Choose one peer for the iteration boundary. Body nodes may target multiple peers.", definition.Id));
        }
        return issues;
    }

    /// <summary>実際に渡された要素結果だけを受け取ります。</summary>
    /// <returns>一要素の値です。全体配列はエンジンが終了後に構成します。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        Dictionary<string, NodeValue> values = context.Inputs.Where(pair => 0 < ValuePortPairs.Index(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.Ordinal);
        if (values.Values.Any(value => value.Kind != EValueKind.Json))
        {
            return ValueTask.FromResult(NodeResult.Failure("iteration.resultType", "Convert the iteration result to JSON before collecting it.", EOutcome.NotExecuted));
        }
        string? control = IterationNodeTypes.RequestedControl(context.Inputs);
        string? reason = null;
        if (control is null)
        {
            values.TryAdd(ValuePortPairs.Name(1), NodeValue.Null);
        }
        else
        {
            reason = "iteration." + control;
        }
        return ValueTask.FromResult(new NodeResult(EOutcome.Succeeded, "success", values, reason));
    }
}
