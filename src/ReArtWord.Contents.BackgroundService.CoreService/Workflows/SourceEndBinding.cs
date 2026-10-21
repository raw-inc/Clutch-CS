using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

namespace ReArtWord.Contents.BackgroundService.CoreService.Workflows;

/// <summary>ワークフローからの正常終了要求を、元の待受を持つPeer実体へ送ります。</summary>
internal sealed class SourceEndBinding(PeerCluster cluster)
{
    /// <summary>入力元の待受IDを確認して受付を止め、後続処理自身の終了待ちは避けます。</summary>
    internal void Register(SourceSupervisor sources)
    {
        cluster.RegisterHandler("source.end", async (request, cancellation) =>
        {
            SourceExecutionInfo? source = request.Payload.Json.Deserialize<SourceExecutionInfo>(ContractJson.Options);
            if (source is null || source.Id == Guid.Empty || source.PeerInstanceId != cluster.LocalInstanceId
                || !string.Equals(source.PeerId, cluster.LocalPeerId, StringComparison.Ordinal))
            {
                return new ClusterResponse(EOutcome.NotExecuted, Reason: "source.identity");
            }
            NodeResult result = await sources.StopAsync(source.WorkflowId, source.NodeId, sourceId: source.Id,
                waitForCompletion: false, cancellationToken: cancellation).ConfigureAwait(false);
            return new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(result));
        });
    }

    /// <summary>同名Peerの再起動後には適用せず、元の実体からの応答だけを採用します。</summary>
    /// <returns>受付終了の要求結果。</returns>
    internal async ValueTask<NodeResult> EndAsync(SourceExecutionInfo source, CancellationToken cancellationToken = default)
    {
        ClusterRequest request = new(Guid.NewGuid(), "source.end", source.PeerId, source.PeerInstanceId!.Value,
            DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.FromObject(source));
        ClusterResponse response = await cluster.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Outcome == EOutcome.Succeeded && response.Value is not null)
        {
            return response.Value.Json.Deserialize<NodeResult>(ContractJson.Options)!;
        }
        return NodeResult.Failure(response.Reason ?? "source.stopUnconfirmed", "元の待受の受付終了を確認できません。", response.Outcome);
    }
}
