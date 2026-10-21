using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;

/// <summary>有限配列の反復開始を通常の実行・安全停止境界で受け付けます。</summary>
public sealed class ForEachNode : IActionNode
{
    /// <summary>実行順だけが異なる二種類を公開します。</summary>
    public ForEachNode(bool parallel = false)
    {
        string type = IterationNodeTypes.ForEach;
        string schema = """{"type":"object","properties":{}}""";
        if (parallel)
        {
            type = IterationNodeTypes.ParallelForEach;
            schema = """{"type":"object","properties":{"maxConcurrency":{"x-runtime-input":false,"type":"integer","minimum":1,"maximum":32,"default":4},"startIntervalMs":{"x-runtime-input":false,"type":"integer","minimum":0,"maximum":2147483646,"default":0}}}""";
        }
        Descriptor = NodeSupport.Describe(type, type, "Control", schema,
            [new("value", EPortValueKind.Json, Required: true), new("elementType", EPortValueKind.Json, OverridesSetting: "elementType")],
            [new("item"), new("index", EPortValueKind.Json), new("count", EPortValueKind.Json), new("start", EPortValueKind.Json), new(IterationNodeTypes.LoopPort, EPortValueKind.Json)]) with { HasAsyncCompletion = true, SettingsSchema = StructuredValueTypes.Schema(ReArtWord.Contents.BackgroundService.Runtime.Configuration.NodeSettings.Schema(schema), "element") };
    }

    /// <summary>配列と要素側の接続契約です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>一つの反復入力を複数の開始Peerへ暗黙展開しません。</summary>
    /// <returns>設定と開始対象の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor), .. StructuredValueTypes.Validate(definition, "element")];
        if (definition.Target is not null && definition.Target.Scope != EExecutionScope.One)
        {
            issues.Add(new("iteration.target", "Choose one peer for the iteration boundary. Body nodes may target multiple peers.", definition.Id));
        }
        return issues;
    }

    /// <summary>副作用を開始する前に配列の種類と上限を検証します。</summary>
    /// <returns>エンジンが所有する独立した配列、または未実行理由です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        NodeValue? value = NodeSupport.Input(context);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        if (value?.Kind != EValueKind.Json || value.Json.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)
            || value.Json.ValueKind == JsonValueKind.Array && 4096 < value.Json.GetArrayLength()
            || value.Json.ValueKind == JsonValueKind.Object && 4096 < value.Json.EnumerateObject().Count())
        {
            return ValueTask.FromResult(NodeResult.Failure("iteration.array", "ForEach requires an array or dictionary with at most 4096 elements.", EOutcome.NotExecuted));
        }
        try
        {
            if (value.Json.ValueKind == JsonValueKind.Object)
            {
                ValueTypeOperations.ValidateValue(value, new("object"));
                value = DictionaryValues.ForEach(value);
            }
            ValueTypeDefinition? element = StructuredValueTypes.Read(context.Settings, context.Inputs, "element");
            if (element is not null)
            {
                ValueTypeOperations.ValidateValue(value, new("array", element));
                value = PathTypeMetadata.Apply(value, new("array", element));
            }
            else
            {
                if (value.DataType is not null)
                {
                    value = PathTypeMetadata.Apply(value, value.DataType);
                }
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return ValueTask.FromResult(NodeResult.Failure("iteration.elementType", "The array does not match the selected element type or JSON Schema. No iteration was started.", EOutcome.NotExecuted));
        }
        return ValueTask.FromResult(new NodeResult(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { [IterationNodeTypes.LoopPort] = value.Copy() }));
    }
}
