using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>エディターから明示開始された一回の押下を、値と開始信号へ変換します。</summary>
public sealed class ButtonNode : IActionNode
{
    /// <summary>ボタンの表示文字列と、押下時に渡す値を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe(ButtonValuePorts.NodeType, "Button", "Control", """{"type":"object","properties":{}}""",
        inputs: [.. Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ButtonValuePorts.Name(index))), new("label", EPortValueKind.Json) { DataType = new("string", Optional: true) }],
        outputs: [.. Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ButtonValuePorts.Name(index))), new("start", EPortValueKind.Json)]) with
    {
        Description = "Press the button in the workflow editor to emit a start signal and the input values. Each populated input adds the next value pair. Automatic execution does not press the button.",
    };

    /// <summary>表示文字列も通常の入力型検証へ含めます。</summary>
    /// <returns>設定と入力に含まれる問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>一回の明示押下で取得した入力を型変換せずに後続へ渡します。</summary>
    /// <returns>値と一件の開始信号、または入力検証の失敗です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        Dictionary<string, NodeValue> outputs = new(StringComparer.Ordinal)
        {
            ["value"] = (NodeSupport.Input(context) ?? NodeValue.Null).Copy(),
            ["start"] = NodeValue.FromObject(true),
        };
        foreach ((string name, NodeValue value) in context.Inputs.Where(pair => 1 < ButtonValuePorts.Index(pair.Key)))
        {
            outputs.Add(name, value.Copy());
        }
        return ValueTask.FromResult(new NodeResult(EOutcome.Succeeded, "success", outputs));
    }
}
