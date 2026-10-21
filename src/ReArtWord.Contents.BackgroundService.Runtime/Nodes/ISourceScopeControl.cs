using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>退避経路が新規開始する待受だけに、作成元が所有する寿命を渡します。</summary>
public interface ISourceScopeControl
{
    /// <summary>現在のPeer実体で一回の待受範囲を開始します。</summary>
    /// <returns>実体を固定した範囲です。</returns>
    public SourceScope Create(string workflowId);

    /// <summary>待受開始前には実行Peerを登録し、終了通知との競合も同じ所有元で判定します。</summary>
    /// <returns>有効・終了済み、または通信の結果不明です。</returns>
    public ValueTask<bool?> CheckAsync(SourceScope scope, bool enroll, CancellationToken cancellationToken = default);

    /// <summary>以後の待受開始を拒否し、この範囲の既存待受を正常終了させます。</summary>
    /// <returns>受付済みの有限処理の終了とは区別した、待受停止の確認です。</returns>
    public ValueTask<NodeResult> EndAsync(SourceScope scope, CancellationToken cancellationToken = default);
}
