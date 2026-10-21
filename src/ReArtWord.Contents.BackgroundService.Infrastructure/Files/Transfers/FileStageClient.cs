using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transfers;

/// <summary>補償段階の初回要求を保存し、再開時は操作記録だけを照会します。</summary>
internal sealed class FileStageClient(PeerCluster cluster, IDurableRepository repository)
{
    /// <summary>副作用のない実行地点の検証を一度問い合わせます。</summary>
    /// <returns>検証応答です。</returns>
    public Task<ClusterResponse> InspectAsync(string kind, PeerInfo target, NodeValue payload, CancellationToken cancellationToken = default) => cluster.InspectAsync(new ClusterRequest(Guid.NewGuid(), kind, target.PeerId, target.InstanceId, DateTimeOffset.UtcNow.AddSeconds(30), payload), cancellationToken);

    /// <summary>固定実体と期限を記録して一度だけ要求本体を送ります。</summary>
    /// <returns>元段階の完了結果または確認不能。</returns>
    public async Task<ClusterResponse> InvokeAsync(Guid id, string kind, PeerInfo target, NodeValue payload, IReadOnlyList<PeerInfo> members, CancellationToken cancellationToken = default)
    {
        bool local = members.Count == 1 && members[0].Status == EPeerStatus.Detached;
        string scope = "file:requests";
        if (local)
        {
            scope = "peer:" + cluster.LocalPeerId + ":file-requests";
        }
        DurableEntry? entry = await repository.ReadAsync(scope, id.ToString("N"), cancellationToken).ConfigureAwait(false);
        ClusterRequest? request = entry?.Value?.Json.Deserialize<ClusterRequest>();
        bool created = request is null;
        if (request is null)
        {
            request = new ClusterRequest(id, kind, target.PeerId, target.InstanceId, DateTimeOffset.UtcNow.AddMinutes(5), payload.Copy());
            DurableMutation[] mutation = [new(scope, id.ToString("N"), null, NodeValue.FromObject(request))];
            DurableReceipt saved;
            if (local)
            {
                saved = await repository.ApplyBatchAsync(Identity(id, "request"), mutation, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                saved = await cluster.ReplicateFixedAsync(Identity(id, "request"), mutation, members, cancellationToken).ConfigureAwait(false);
            }
            if (saved.Outcome != EOutcome.Succeeded)
            {
                return new ClusterResponse(EOutcome.Unknown, Reason: "File.StageRequestUnconfirmed");
            }
        }
        if (!string.Equals(request.Kind, kind, StringComparison.Ordinal) || request.TargetInstanceId != target.InstanceId || !string.Equals(ContractJson.Hash(request.Payload), ContractJson.Hash(payload), StringComparison.Ordinal))
        {
            return new ClusterResponse(EOutcome.Failed, Reason: "File.StageMismatch");
        }
        ClusterResponse response = new(EOutcome.Unknown, Reason: "Cluster.ResponseUnconfirmed");
        if (created)
        {
            response = await cluster.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        return await RemoteNodeCompletion.ConfirmAsync(cluster, request, response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>段階の一件識別子を元の実行IDから固定します。</summary>
    /// <returns>元操作と段階に固有のID。</returns>
    public static Guid Identity(Guid id, string phase) => new(Convert.FromHexString(ContractJson.Hash(new { id, phase })).AsSpan(0, 16));
}
