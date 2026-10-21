namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>入力ごとの値を、一件ずつ合流するノードの契約です。</summary>
public static class ValueMergePorts
{
    /// <summary>保存とカタログで使用する種類識別子です。</summary>
    public static string NodeType => "control.valueMerge";

    /// <summary>値の合流ノードを判定します。</summary>
    /// <returns>到着ごとに独立した後続を開始する値ノードかです。</returns>
    public static bool IsNode(string type) => string.Equals(type, NodeType, StringComparison.Ordinal);
}
