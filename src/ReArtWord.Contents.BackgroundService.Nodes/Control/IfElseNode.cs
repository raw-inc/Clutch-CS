using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>一回の真偽判定から、対応する一方の開始信号と判定値を出力します。</summary>
public sealed class IfElseNode : IActionNode
{
    /// <summary>二つの開始口と、通常データとして参照できる真偽値です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("control.if", "If", "Control", """{"type":"object","properties":{}}""",
        inputs: [new("condition", EPortValueKind.Json, Required: true) { DataType = new("boolean") }],
        outputs: [new("trueStart", EPortValueKind.Json), new("falseStart", EPortValueKind.Json), new("value", EPortValueKind.Json)]) with
    {
        Description = "Evaluate one boolean and emit only the matching True or False start signal. Also output the condition as a boolean value."
    };

    /// <summary>開始前に入力と設定の契約を検証します。</summary>
    /// <returns>設定上の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>未選択側の出力を省略し、偽の判定でも選択側の開始信号はtrueを渡します。</summary>
    /// <returns>選択した一口の開始信号と、元の真偽値です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        NodeValue? condition = NodeSupport.Input(context, "condition");
        if (condition?.Kind != EValueKind.Json || condition.Json.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return ValueTask.FromResult(NodeResult.Failure("condition.boolean.required", "If requires a boolean condition; strings, numbers and null are not converted."));
        }
        string selected = condition.Json.GetBoolean() ? "trueStart"
                                                      : "falseStart";
        return ValueTask.FromResult(new NodeResult(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            [selected] = NodeValue.FromObject(true), ["value"] = condition.Copy(),
        }));
    }
}
