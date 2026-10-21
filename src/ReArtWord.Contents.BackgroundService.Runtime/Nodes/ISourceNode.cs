using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>入力を受けて継続待受を開始するノードです。</summary>
public interface ISourceNode : INode
{
    /// <summary>一件ずつ結果を通知し、終了時に待受自体の結果を返します。</summary>
    /// <returns>予定正常終了、取消、失敗または結果不明を区別した終了結果です。</returns>
    public Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default);
}
