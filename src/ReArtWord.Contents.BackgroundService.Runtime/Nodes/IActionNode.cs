using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>一回の入力から一回の結果を返すノードです。</summary>
public interface IActionNode : INode
{
    /// <summary>開始時の設定と入力で処理します。</summary>
    /// <returns>この処理自身の結果です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default);
}
