using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>明示したUTF-16の範囲から部分文字列を取得します。</summary>
public sealed class SubstringNode : PureValueNode
{
    /// <summary>位置・長さも他ノードから受け取る文字列処理です。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("text.substring", "SubString", "Data", """{"type":"object","properties":{}}""",
        inputs: [new("value", EPortValueKind.Json, true), new("startIndex", EPortValueKind.Json, true), new("length", EPortValueKind.Json)],
        outputs: [new("value", EPortValueKind.Json)]) with
        {
            Description = "Extract a substring using zero-based C# UTF-16 indices. Omit length for the remainder; invalid ranges fail."
        };

    /// <summary>通常実行と定数評価で同じ範囲検証を使用します。</summary>
    /// <returns>指定範囲の文字列です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
        => SubstringValues.Extract(inputs.GetValueOrDefault("value"), inputs.GetValueOrDefault("startIndex"), inputs.GetValueOrDefault("length"));
}
