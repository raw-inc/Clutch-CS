using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;

namespace ReArtWord.Contents.BackgroundService.CoreService.Workflows;

/// <summary>実行を所有するPeerから表示先だけへ、非永続の進捗終了を確認付きで配送します。</summary>
internal static class WorkflowProgressBinding
{
    private static string Kind => "progress.complete";

    /// <summary>通常のノードを再実行せず、表示状態だけを同じ実体から解放します。</summary>
    internal static void Register(PeerCluster cluster, WorkflowEngine engine, NodePreviewStore previews)
    {
        cluster.RegisterInspection(Kind, (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            ProgressSession session = request.Payload.Json.Deserialize<ProgressSession>() ?? throw new InvalidDataException("進捗の終了対象がありません。");
            if (session.RunId == Guid.Empty || !cluster.GetPeers().Any(peer => peer.InstanceId == session.OwnerInstanceId && string.Equals(peer.PeerId, session.OwnerPeerId, StringComparison.Ordinal)))
            {
                return Task.FromResult(new ClusterResponse(EOutcome.NotExecuted));
            }
            previews.CompleteProgress(session);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded));
        });
        engine.CompleteProgress = async (session, peers, token) =>
        {
            await Task.WhenAll(peers.Select(peer => CompleteAsync(cluster, previews, session, peer, token))).ConfigureAwait(false);
        };
    }

    /// <summary>ローカルも遠隔も同じ終了識別を使い、別実行や再起動後へ広げません。</summary>
    /// <returns>表示先の解放確認です。</returns>
    private static async Task CompleteAsync(PeerCluster cluster, NodePreviewStore previews, ProgressSession session, PeerInfo peer, CancellationToken cancellationToken = default)
    {
        if (peer.InstanceId == cluster.LocalInstanceId)
        {
            previews.CompleteProgress(session);
            return;
        }
        ClusterRequest request = new(Guid.NewGuid(), Kind, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(2), NodeValue.FromObject(session));
        _ = await cluster.InspectAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
