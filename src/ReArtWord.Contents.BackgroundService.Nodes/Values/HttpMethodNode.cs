using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>HTTPメソッドを値として選択し、外部要求は行いません。</summary>
public sealed class HttpMethodNode : PureValueNode
{
    /// <summary>一般的な七つの明示メソッドを宣言します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("http.method", "HTTP method", "Values",
        """{"type":"object","required":["method"],"properties":{"method":{"type":"string","enum":["GET","POST","PUT","PATCH","DELETE","HEAD","OPTIONS"]}}}""",
        inputs: [], outputs: [new("value", EPortValueKind.Json)]) with { Description = "Choose an HTTP method as a string value without making a request." };

    /// <summary>検証済みのメソッド文字列だけを返します。</summary>
    /// <returns>JSON文字列です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs) => NodeValue.FromObject(new NodeSettings(definition.Settings).ReadText("method"));
}
