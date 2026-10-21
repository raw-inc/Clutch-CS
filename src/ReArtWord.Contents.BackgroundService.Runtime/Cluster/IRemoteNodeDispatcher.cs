using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Cluster;

/// <summary>指定したPeer実体へ同一実行IDの要求を配送します。</summary>
public interface IRemoteNodeDispatcher
{
    /// <summary>要求の実施結果を確認します。結果不明を新実行として再試行しません。</summary>
    /// <returns>対象が確認した結果、または確認不能の結果です。</returns>
    public Task<NodeResult> ExecuteAsync(RemoteExecutionRequest request, CancellationToken cancellationToken = default);
}
