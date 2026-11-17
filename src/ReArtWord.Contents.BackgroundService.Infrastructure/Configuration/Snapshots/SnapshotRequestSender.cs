using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>復元各段階の期限と実体を最初の配送から変更しません。</summary>
internal sealed class SnapshotRequestSender(IDurableRepository repository, PeerCluster cluster)
{
    private readonly SnapshotRequestDelivery delivery = new(repository,
        (request, token) => cluster.RequestClocks.PrepareAsync(request, cluster.InspectAsync, token));

    /// <summary>同じ段階は保存した要求を照会し、実処理を再作成しません。</summary>
    /// <returns>対象の確認結果。</returns>
    public async Task<ClusterResponse> SendAsync(string phase, string identityPhase, Guid operationId, SnapshotTarget target, PeerInfo peer, NodeValue? value, CancellationToken cancellationToken = default)
        => await SendStepAsync(phase, identityPhase, peer, new SnapshotStep(operationId, target, value), cancellationToken).ConfigureAwait(false);

    /// <summary>組合せを含む固定準備要求を保存して送ります。</summary>
    /// <returns>対象の確認結果。</returns>
    public async Task<ClusterResponse> SendStepAsync(string phase, string identityPhase, PeerInfo peer, SnapshotStep step, CancellationToken cancellationToken = default)
    {
        SnapshotTarget target = step.Target;
        Guid id = WorkflowStore.PhaseId(step.OperationId, ContractJson.Hash(new { target, phase = identityPhase }));
        NodeValue payload = NodeValue.FromObject(step);
        string scope = "cluster:snapshot-requests";
        ClusterRequest proposed = ClusterRequest.Start(id, "snapshot." + phase, target.PeerId, peer.InstanceId, TimeSpan.FromSeconds(30), payload);
        ClusterRequest? request = await delivery.PrepareAsync(scope, proposed, cancellationToken).ConfigureAwait(false);
        if (request is null || !string.Equals(ContractJson.Hash(request.Payload), ContractJson.Hash(payload), StringComparison.Ordinal) || request.TargetInstanceId != peer.InstanceId)
        {
            return new ClusterResponse(EOutcome.Failed, Reason: "Snapshot.OperationMismatch");
        }
        return await cluster.RequestAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
