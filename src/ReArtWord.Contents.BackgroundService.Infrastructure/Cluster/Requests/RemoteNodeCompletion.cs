using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>開始済みの長い遠隔実行を読取り専用照会で追跡します。</summary>
internal static class RemoteNodeCompletion
{
    /// <summary>ローカル受付と遠隔配送で同じ要求実体を使用します。</summary>
    /// <returns>一度の配送による確認結果。</returns>
    public static async Task<ClusterResponse> SendAsync(PeerCluster cluster, ClusterRequest request, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> local, string kind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ContractJson.Copy(request);
        if (string.Equals(request.TargetPeerId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            return await local(request, cancellationToken).ConfigureAwait(false);
        }
        Guid instance = cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, request.TargetPeerId, StringComparison.Ordinal))?.InstanceId ?? request.TargetInstanceId;
        string inspection = "inspect";
        if (string.Equals(kind, inspection, StringComparison.Ordinal))
        {
            instance = request.TargetInstanceId;
        }
        WireMessage? response = await cluster.ExchangeAsync(kind, request.TargetPeerId, instance, JsonSerializer.SerializeToElement(request), cancellationToken).ConfigureAwait(false);
        return response?.Body.Deserialize<ClusterResponse>() ?? new ClusterResponse(EOutcome.Unknown, Reason: "Cluster.ResponseUnconfirmed");
    }

    /// <summary>読み取り専用のローカルまたは遠隔照会を配送します。</summary>
    /// <returns>実行中・保存済み結果・確認不能の区別。</returns>
    public static async Task<ClusterRequestStatus> QueryAsync(PeerCluster cluster, RequestExecutor executor, ClusterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestQuery query = new(request.Id, ContractJson.Hash(request));
        if (string.Equals(request.TargetPeerId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            return await executor.ReadStatusAsync(query, cancellationToken).ConfigureAwait(false);
        }
        Guid instance = cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, request.TargetPeerId, StringComparison.Ordinal))?.InstanceId ?? request.TargetInstanceId;
        WireMessage? response = await cluster.ExchangeAsync("receipt.query", request.TargetPeerId, instance, JsonSerializer.SerializeToElement(query), cancellationToken).ConfigureAwait(false);
        return response?.Body.Deserialize<ClusterRequestStatus>() ?? new ClusterRequestStatus(false, Reason: "Execution.ReceiptUnconfirmed");
    }

    /// <summary>要求は一度だけ配送し、応答待ち期限後は操作記録だけを読みます。</summary>
    /// <returns>元の完了結果または確認不能結果。</returns>
    public static async Task<NodeResult> ExecuteAsync(PeerCluster cluster, RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cluster.IsDetached && !string.Equals(request.TargetPeerId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            return NodeResult.Failure("Detached.RemoteExecutionNotAllowed", "明示離脱中の実行先はこのPeerだけです。", EOutcome.NotExecuted);
        }
        ClusterRequest command = new(request.Id, "node.execute", request.TargetPeerId, request.TargetInstanceId, request.ExpiresAt, NodeValue.FromObject(request));
        ClusterResponse response = await cluster.RequestAsync(command, cancellationToken).ConfigureAwait(false);
        response = await ConfirmAsync(cluster, command, response, cancellationToken).ConfigureAwait(false);
        return response.Value?.Json.Deserialize<NodeResult>() ?? NodeResult.Failure(response.Reason ?? "Execution.Unconfirmed", "実行結果を確認できません。", response.Outcome);
    }

    /// <summary>配送確認を失った同じ要求の結果だけを照会し、受付や副作用を再発行しません。</summary>
    /// <returns>元実体の完了結果、実体喪失、または継続して確認できない理由です。</returns>
    internal static async Task<ClusterResponse> ConfirmAsync(PeerCluster cluster, ClusterRequest request, ClusterResponse response, CancellationToken cancellationToken = default)
    {
        string unconfirmed = "Cluster.ResponseUnconfirmed";
        string running = "Execution.Running";
        long? missingSince = null;
        while (string.Equals(response.Reason, unconfirmed, StringComparison.Ordinal) || string.Equals(response.Reason, running, StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cluster.GetPeers().Any(peer => string.Equals(peer.PeerId, request.TargetPeerId, StringComparison.Ordinal) && peer.InstanceId == request.TargetInstanceId))
            {
                response = new ClusterResponse(EOutcome.Unknown, Reason: "Execution.TargetInstanceLost");
                break;
            }
            ClusterRequestStatus status = await cluster.QueryRequestAsync(request, cancellationToken).ConfigureAwait(false);
            if (status.Response is not null)
            {
                response = status.Response;
                break;
            }
            if (!status.Running)
            {
                bool transient = status.Reason is "Execution.ReceiptMissing" or "Execution.ReceiptUnconfirmed";
                missingSince ??= TimeProvider.System.GetTimestamp();
                if (!transient || TimeSpan.FromSeconds(10) <= TimeProvider.System.GetElapsedTime(missingSince.Value))
                {
                    response = new ClusterResponse(EOutcome.Unknown, Reason: status.Reason ?? "Execution.ReceiptUnconfirmed");
                    break;
                }
            }
            else
            {
                missingSince = null;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        return response;
    }
}
