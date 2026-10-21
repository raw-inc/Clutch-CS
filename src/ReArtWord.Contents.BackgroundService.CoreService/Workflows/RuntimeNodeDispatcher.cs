using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;

namespace ReArtWord.Contents.BackgroundService.CoreService.Workflows;

/// <summary>同一プロセスの通常実行は直接渡し、遠隔実行だけを実体固定の通信へ渡します。</summary>
internal sealed class RuntimeNodeDispatcher(PeerCluster cluster) : IRemoteNodeDispatcher
{
    /// <summary>Engineが一回の所有タスクとして確定した要求を、同じ取消・安全確認へ接続します。</summary>
    /// <returns>ノード本体の確認済み結果です。</returns>
    public Task<NodeResult> ExecuteAsync(RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.TargetPeerId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            if (!cluster.IsDetached && !cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status)))
            {
                return Task.FromResult(NodeResult.Failure("Cluster.TargetNotReady", "The fixed target is not participating.", EOutcome.NotExecuted));
            }
            return cluster.RemoteExecutionHandler?.Invoke(request, cancellationToken) ?? Task.FromResult(NodeResult.Failure("execution.unavailable", "The local executor is not ready.", EOutcome.NotExecuted));
        }
        return cluster.ExecuteAsync(request with { BufferedReceipt = true }, cancellationToken);
    }
}
