using ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>データ処理ノードを列挙します。</summary>
public static class DataNodeRegistration
{
    /// <summary>六形式と抽出・基本構造変換を登録します。</summary>
    /// <returns>独立して追加できるノードの一覧です。</returns>
    public static IReadOnlyList<INode> Create()
    {
        List<INode> nodes = [];
        foreach (string format in new[] { "json", "yaml", "toml", "csv", "text", "binary" })
        {
            nodes.Add(new SerializationNode(format, false));
            nodes.Add(new SerializationNode(format, true));
        }
        nodes.Add(new ConcatNode());
        nodes.Add(new ExpandObjectNode());
        nodes.Add(new ConvertNode());
        nodes.Add(new TextDecodeNode());
        nodes.Add(new TextEncodeNode());
        nodes.Add(new Base64DecodeNode());
        nodes.Add(new KeyValueLookupNode());
        nodes.Add(new DefaultIfNullNode());
        nodes.Add(new IsNullNode());
        nodes.Add(new KeyValuePartNode(true));
        nodes.Add(new KeyValuePartNode(false));
        nodes.Add(new CountNode());
        nodes.Add(new PickNode());
        nodes.Add(new RandomPickNode());
        nodes.Add(new SubstringNode());
        nodes.Add(new RegexNode());
        nodes.Add(new RegexNode(true));
        nodes.Add(new ExtractNode());
        nodes.Add(new TransformNode());
        foreach (string operation in new[] { "join", "group", "flatten", "expand", "split" })
        {
            nodes.Add(new CollectionNode(operation));
        }
        return nodes;
    }
}
