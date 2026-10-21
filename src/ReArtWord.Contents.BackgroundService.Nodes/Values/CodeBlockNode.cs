using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>実行せずにコード文字列を後続のスクリプトノードへ渡します。</summary>
public sealed class CodeBlockNode : PureValueNode
{
    /// <summary>言語選択とコード編集、解析に使用する外部ランタイムを宣言します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("value.code", "Code block", "Values", $$$$"""
        {"type":"object","required":["code"],"properties":{
        "language":{"type":"string","enum":{{{{JsonSerializer.Serialize(CodeBlockLanguages.All)}}}},"default":"text","x-connectable":false},
        "code":{"type":"string","default":"","maxLength":65536,"x-script-code":true,"x-connectable":false},
        "pythonPath":{"type":"string"},"nodePath":{"type":"string"},"analysis":{"type":"object","x-script-metadata":true,"x-connectable":false},
        "manualContract":{"type":"object","x-script-metadata":true,"x-connectable":false}}}
        """, inputs: [], outputs: [new("value", EPortValueKind.Json)]) with
    {
        Description = "Edit highlighted code or configuration text without executing it. C#, Python and JavaScript also support syntax checks.",
    };

    /// <summary>改行を含む元のコードをそのまま返します。</summary>
    /// <returns>実行可能性とは分離した文字列値です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
        => NodeValue.FromObject(new NodeSettings(definition.Settings).ReadText("code")!);
}
