using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>監視処理管理をPeer参加から分け、既存照会と永続実行へ接続します。</summary>
public sealed class PeerManagementEndpoint
{
    private readonly PeerCluster cluster;
    private readonly Func<ManagementCommand, CancellationToken, Task<ManagementReply>> handler;
    private readonly Func<string, bool> readOnlyOperation;
    private readonly Func<string, bool>? transientOperation;

    /// <summary>HTTProuterではなく登録済みドメイン操作へ直接接続します。</summary>
    public PeerManagementEndpoint(PeerCluster cluster, Func<ManagementCommand, CancellationToken, Task<ManagementReply>> handler, Func<string, bool> isReadOnlyOperation, Func<string, bool>? isTransientOperation = null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(isReadOnlyOperation);
        this.cluster = cluster;
        this.handler = handler;
        readOnlyOperation = isReadOnlyOperation;
        transientOperation = isTransientOperation;
        cluster.RegisterInspection(ManagementWire.ReadKind, InspectAsync);
        cluster.RegisterInspection(ManagementWire.TransientKind, NotifyAsync);
        cluster.RegisterHandler(ManagementWire.ExecuteKind, ExecuteAsync);
        cluster.ManagementRequestsEnabled = true;
    }

    /// <summary>読取りとして登録された操作以外を照会経路から起動しません。</summary>
    /// <returns>現在の読取結果です。</returns>
    private Task<ClusterResponse> InspectAsync(ClusterRequest request, CancellationToken cancellationToken = default) => DispatchAsync(request, true, false, cancellationToken);

    /// <summary>変更多段階操作を一つの外側操作記録で扱います。</summary>
    /// <returns>元の操作結果です。</returns>
    private Task<ClusterResponse> ExecuteAsync(ClusterRequest request, CancellationToken cancellationToken = default) => DispatchAsync(request, false, false, cancellationToken);

    /// <summary>短命状態だけを非永続で受け付けます。</summary>
    /// <returns>通知結果です。</returns>
    private Task<ClusterResponse> NotifyAsync(ClusterRequest request, CancellationToken cancellationToken = default) => DispatchAsync(request, false, true, cancellationToken);

    /// <summary>外側宛先・期限・操作分類を照合してからドメインへ渡します。</summary>
    /// <returns>元サービスの結果です。</returns>
    private async Task<ClusterResponse> DispatchAsync(ClusterRequest request, bool inspection, bool transient, CancellationToken cancellationToken = default)
    {
        ManagementCommand? command = request.Payload.Json.Deserialize<ManagementCommand>(ContractJson.Options);
        if (command is null || command.RequestId == Guid.Empty || command.RequestId != request.Id || command.TargetInstanceId != request.TargetInstanceId
            || command.ExpiresAt != request.ExpiresAt || !string.Equals(command.TargetPeerId, request.TargetPeerId, StringComparison.Ordinal)
            || !string.Equals(command.ClusterId, cluster.LocalClusterId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(command.Operation))
        {
            return new(EOutcome.NotExecuted, Reason: "Management.EnvelopeMismatch");
        }
        if (readOnlyOperation(command.Operation) != inspection || (transientOperation?.Invoke(command.Operation) == true) != transient)
        {
            return new(EOutcome.NotExecuted, Reason: "Management.OperationModeMismatch");
        }
        ManagementReply reply = await handler(command, cancellationToken).ConfigureAwait(false);
        return new(reply.Outcome, NodeValue.FromObject(reply), reply.Reason);
    }
}
