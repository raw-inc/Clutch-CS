using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>通常の全入力待ちから切り離して、一件ごとの到着を配送する合流口です。</summary>
public static class EventMergePorts
{
    /// <summary>信号合流と値合流で共通の編集規則を使用します。</summary>
    /// <returns>番号付きの到着入力を持つノードかです。</returns>
    public static bool IsNode(string type) => SignalMergePorts.IsNode(type) || ValueMergePorts.IsNode(type);

    /// <summary>要求内の先着合流を除き、到着ごとの実行境界を識別します。</summary>
    /// <returns>通常の依存評価で直接実行しないノードかです。</returns>
    public static bool IsBoundary(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return ValueMergePorts.IsNode(node.Type) || SignalMergePorts.IsEventBoundary(node);
    }
}
