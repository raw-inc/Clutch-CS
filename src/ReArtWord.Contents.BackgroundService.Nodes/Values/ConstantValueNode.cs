using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>元のtextを指定型へ明示変換します。</summary>
public sealed class ConstantValueNode : PureValueNode
{
    /// <summary>省略時のautoと任意の型入力を宣言します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("value.constant", "Constant value", "Values",
        """{"type":"object","required":["text"],"properties":{"text":{"type":"string","maxLength":1048576},"type":{"type":"string","enum":["auto","any","boolean","byte","integer","int64","number","decimal","char","string","path","array","object"]}}}""",
        inputs: [new("type", EPortValueKind.Json, OverridesSetting: "type")], outputs: [new("value", EPortValueKind.Json)]) with { Description = "Construct a value from text. Auto preserves valid JSON; typed conversion rejects invalid ranges and decimal precision loss." };

    /// <summary>型入力を優先し、未指定autoだけ非JSONを文字列として扱います。</summary>
    /// <returns>精度検証済みの値です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        string typePort = "type";
        NodeSettings settings = new(definition.Settings);
        string text = settings.ReadText("text")!;
        if (inputs.TryGetValue(typePort, out NodeValue? supplied))
        {
            return ValueTypeOperations.ParseText(text, ValueTypeOperations.ReadType(supplied));
        }
        string type = settings.ReadText("type", "auto")!;
        string automatic = "auto";
        if (string.Equals(type, automatic, StringComparison.Ordinal))
        {
            return ValueTypeOperations.ParseAuto(text);
        }
        return ValueTypeOperations.ParseText(text, new ValueTypeDefinition(type));
    }
}
