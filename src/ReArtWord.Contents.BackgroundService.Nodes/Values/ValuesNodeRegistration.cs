using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>純粋な値構築ノードを登録します。</summary>
public static class ValuesNodeRegistration
{
    /// <summary>同じ評価契約を持つ各型を列挙します。</summary>
    /// <returns>通常registryと設定用評価で共有するノードです。</returns>
    public static IReadOnlyList<INode> Create() => [new ConstantValueNode(), new CommentNode(), new CodeBlockNode(), new TypeValueNode(), new ArrayValueNode(), new DictionaryValueNode(), new KeyValueNode(), new HttpMethodNode(), new InterfaceValueNode(), new PeerValueNode(), new PathValueNode(false), new PathValueNode(true)];
}
