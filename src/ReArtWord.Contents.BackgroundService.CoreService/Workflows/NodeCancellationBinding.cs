using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.CoreService.Workflows;

/// <summary>実行の明示取消を、元のノード実体への取消要求に接続します。</summary>
internal static class NodeCancellationBinding
{
    /// <summary>取消を通常の接続終了や別実体への再配送から分離します。</summary>
    internal static void Register(PeerCluster cluster, WorkflowEngine engine, NodeExecutor executor)
    {
        cluster.RegisterHandler("node.cancel", async (request, cancellation) =>
        {
            NodeCancellationRequest? target = request.Payload.Json.Deserialize<NodeCancellationRequest>(ContractJson.Options);
            if (target is null || target.ExecutionId == Guid.Empty)
            {
                return new ClusterResponse(EOutcome.NotExecuted, Reason: "Execution.InvalidCancellation");
            }
            bool accepted = await executor.RequestCancellationAsync(target.ExecutionId, target.StartBy, cancellation).ConfigureAwait(false);
            return new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(accepted));
        });
        engine.NodeCancellationHandler = (original, cancellation) => SendAsync(cluster, original, cancellation);
    }

    /// <summary>受付応答を実停止成功へ読み替えず、同じPeer実体にだけ要求を渡します。</summary>
    /// <returns>取消を受け付けたと確認できた場合だけtrueです。</returns>
    private static async Task<bool> SendAsync(PeerCluster cluster, RemoteExecutionRequest original, CancellationToken cancellationToken = default)
    {
        ClusterRequest request = new(Guid.NewGuid(), "node.cancel", original.TargetPeerId, original.TargetInstanceId,
            DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.FromObject(new NodeCancellationRequest(original.Id, original.ExpiresAt)));
        ClusterResponse response = await cluster.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Outcome == EOutcome.Succeeded && response.Value?.Kind == EValueKind.Json
            && response.Value.Json.ValueKind == JsonValueKind.True;
    }
}
