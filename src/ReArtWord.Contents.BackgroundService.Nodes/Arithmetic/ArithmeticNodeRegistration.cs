using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;

/// <summary>四則演算と累乗・累乗根・乱数を個別のノード種類として公開します。</summary>
public static class ArithmeticNodeRegistration
{
    /// <summary>演算だけを固定した共通実装を登録します。</summary>
    /// <returns>数値の計算と生成を行うNodeです。</returns>
    public static IReadOnlyList<INode> Create() => [new ArithmeticNode("add"), new ArithmeticNode("subtract"), new ArithmeticNode("multiply"), new ArithmeticNode("divide"), new PowerNode("power"), new PowerNode("root"), new RandomNode()];
}
