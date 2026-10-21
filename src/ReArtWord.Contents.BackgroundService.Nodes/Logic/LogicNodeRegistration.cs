using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Logic;

/// <summary>JSON真偽値・整数の単項・二項・配列論理演算を公開します。</summary>
public static class LogicNodeRegistration
{
    /// <summary>演算を固定した八つの論理ノードを登録します。</summary>
    /// <returns>AND、OR、NOT、NAND、NOR、XOR、ANDS、ORSの8Nodeです。</returns>
    public static IReadOnlyList<INode> Create() => [new LogicNode("and"), new LogicNode("or"), new LogicNode("not"), new LogicNode("nand"), new LogicNode("nor"), new LogicNode("xor"), new LogicNode("ands"), new LogicNode("ors")];
}
