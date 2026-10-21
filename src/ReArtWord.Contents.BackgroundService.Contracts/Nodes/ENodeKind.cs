namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>ノードの実行寿命です。</summary>
public enum ENodeKind
{
    /// <summary>一回の入力に一回の結果を返します。</summary>
    Action,

    /// <summary>継続中に複数の入力事象を通知します。</summary>
    Source,
}

