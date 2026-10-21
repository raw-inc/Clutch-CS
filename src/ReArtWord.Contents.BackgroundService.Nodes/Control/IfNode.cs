using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>If-Startはtrue、IfNot-Startはfalseのときだけ開始信号を後続へ渡します。</summary>
public sealed class IfNode(bool inverted = false) : IActionNode
{
    /// <summary>比較ノードの真偽値を受け取り、データと分離した開始信号を出力します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe(Type(inverted), Name(inverted), "Control", """{"type":"object","properties":{}}""",
        inputs: [new("condition", EPortValueKind.Json, Required: true)], outputs: [new("start", EPortValueKind.Json)])
        with { Description = "Emit a start signal only when the boolean matches this gate. Non-matching conditions skip the branch; non-boolean conditions fail." };

    /// <summary>反転指定から保存用のノード識別子を決めます。</summary>
    /// <returns>対応するノード種別です。</returns>
    private static string Type(bool inverted) => inverted ? "control.ifNotStart"
                                                          : "control.ifStart";

    /// <summary>開始制御であることを表示名に含めます。</summary>
    /// <returns>カタログ上の名前です。</returns>
    private static string Name(bool inverted) => inverted ? "IfNot-Start"
                                                          : "If-Start";

    /// <summary>通常の設定検証を利用します。</summary>
    /// <returns>設定上の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>非選択と不正な入力を区別し、非選択時はsuccess経路へ何も供給しません。</summary>
    /// <returns>通過値、意図した未実行、または条件の型違反です。</returns>
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
            return ValueTask.FromResult(NodeResult.Failure("condition.boolean.required", "If-Start / IfNot-Start requires a boolean condition; strings, numbers and null are not converted."));
        }
        if (condition.Json.GetBoolean() == inverted)
        {
            return ValueTask.FromResult(NodeResult.Failure("condition.notMatched", "The condition does not match this gate; this branch is skipped.", EOutcome.NotExecuted));
        }
        return ValueTask.FromResult(new NodeResult(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["start"] = NodeValue.FromObject(true) }));
    }
}
