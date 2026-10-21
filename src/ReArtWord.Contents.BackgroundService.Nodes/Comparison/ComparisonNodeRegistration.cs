using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Comparison;

/// <summary>比較を真偽値として取り出すノードを登録します。</summary>
public static class ComparisonNodeRegistration
{
    /// <summary>大小比較・一致判定・不一致判定を作ります。</summary>
    /// <returns>Mathカテゴリの3ノードです。</returns>
    public static IReadOnlyList<INode> Create() => [new ComparisonNode("compare"), new ComparisonNode("equal"), new ComparisonNode("notEqual")];
}
