using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>接続の追加時刻ではなく、入力番号の順に文字列または同型配列を結合します。</summary>
public sealed class ConcatNode : PureValueNode
{
    /// <summary>保存時の番号を固定した入力を公開し、編集画面では空き口を逐次表示します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe(ConcatPorts.NodeType, "Concat", "Data", """{"type":"object","properties":{}}""",
        inputs: [new("separator", EPortValueKind.Json) { DataType = new("string", Optional: true) },
            .. Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ConcatPorts.Name(index), EPortValueKind.Json))])
        with { Description = "Concatenate strings or matching arrays in input-number order. Optional separator text is inserted only between string inputs; omitted or null means empty. Filled inputs reveal the next port; unused inputs are ignored." };

    /// <summary>空き口を無視し、接続・直接入力を同じ順序で扱います。</summary>
    /// <returns>一つの文字列または配列です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
        => ConcatValues.Join(inputs.Where(pair => 0 < ConcatPorts.Index(pair.Key)).OrderBy(pair => ConcatPorts.Index(pair.Key)).Select(pair => pair.Value).ToArray(), inputs.GetValueOrDefault("separator"));
}
