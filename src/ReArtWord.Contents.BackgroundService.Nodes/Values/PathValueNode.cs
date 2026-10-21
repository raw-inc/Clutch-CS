using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>指定ピアで選んだファイル・ディレクトリのパスを設定値として共有します。</summary>
public sealed class PathValueNode : PureValueNode
{
    /// <summary>ファイルとディレクトリを区別する選択ノードを構築します。</summary>
    public PathValueNode(bool directory)
    {
        string type = "value.file";
        string name = "File path";
        if (directory)
        {
            type = "value.directory";
            name = "Directory path";
        }
        Descriptor = NodeSupport.Describe(type, name, "Values",
            """{"type":"object","required":["path"],"properties":{"peerId":{"type":"string","description":"Peer used to browse paths. Choose the same peer as the consumer, or leave unset to use its execution PC."},"path":{"type":"string","minLength":1,"description":"An absolute path on the selected peer. Browse an existing path or type a new destination. The path alone does not change the execution PC."}}}""",
            inputs: [], outputs: [new("value", EPortValueKind.Json)]) with { Description = "Choose a path on a peer or type it directly. Connect it to a file setting on the same PC; this node does not transfer files or change where a consumer runs." };
    }

    /// <summary>パス文字列の固定出力です。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>存在確認を実行時へ残し、新規作成先も指定できるようにします。</summary>
    /// <returns>選択されたパスです。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
        => PathValues.Create(new NodeSettings(definition.Settings).ReadText("path")!);
}
