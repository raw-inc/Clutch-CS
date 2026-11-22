using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using System.Text.Json;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, (Guid Instance, EDerivedIdFormat Format)> formats = new(StringComparer.Ordinal);

    /// <summary>段階要求に生成方式を保持できる実体かを確認する読取り専用操作です。</summary>
    internal static string IdentityCapability => "snapshot.derived-identities";

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
        string scope = "cluster:snapshot-requests";
        string kind = "snapshot." + phase;
        ClusterRequest? proposed = await delivery.ReadOriginalAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (proposed is null)
        {
            EDerivedIdFormat format = await FormatAsync(peer, cancellationToken).ConfigureAwait(false);
            NodeValue payload = NodeValue.FromObject(DerivedIdentityPayload.Write(JsonSerializer.SerializeToElement(step), format));
            proposed = ClusterRequest.Start(id, kind, target.PeerId, peer.InstanceId, TimeSpan.FromSeconds(30), payload);
        }
        SnapshotStep? original = DerivedIdentityPayload.Read(proposed.Payload.Json).Value.Deserialize<SnapshotStep>();
        if (original is null || !string.Equals(ContractJson.Hash(original), ContractJson.Hash(step), StringComparison.Ordinal)
            || !string.Equals(proposed.Kind, kind, StringComparison.Ordinal) || proposed.TargetInstanceId != peer.InstanceId
            || !string.Equals(proposed.TargetPeerId, target.PeerId, StringComparison.Ordinal))
        {
            return new ClusterResponse(EOutcome.Failed, Reason: "Snapshot.OperationMismatch");
        }
        ClusterRequest? request = await delivery.PrepareAsync(scope, proposed, cancellationToken).ConfigureAwait(false);
        if (request is null || !string.Equals(ContractJson.Hash(request.Payload), ContractJson.Hash(proposed.Payload), StringComparison.Ordinal) || request.TargetInstanceId != peer.InstanceId)
        {
            return new ClusterResponse(EOutcome.Failed, Reason: "Snapshot.OperationMismatch");
        }
        return await cluster.RequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>旧版には従来の本文を送り、方式を知っている実体には元操作の方式を伝えます。</summary>
    /// <returns>この実体へ初回送信するときの形式です。通信失敗を未対応とはみなしません。</returns>
    private async Task<EDerivedIdFormat> FormatAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        if (DerivedIdentityScope.Current == EDerivedIdFormat.Legacy)
        {
            return EDerivedIdFormat.Legacy;
        }
        if (formats.TryGetValue(peer.PeerId, out (Guid Instance, EDerivedIdFormat Format) known) && known.Instance == peer.InstanceId)
        {
            return known.Format;
        }
        ClusterRequest query = ClusterRequest.Start(Guid.NewGuid(), IdentityCapability, peer.PeerId, peer.InstanceId, TimeSpan.FromSeconds(30), NodeValue.Null);
        ClusterResponse response = await cluster.InspectAsync(query, cancellationToken).ConfigureAwait(false);
        EDerivedIdFormat format;
        if (response.Outcome == EOutcome.Succeeded && response.Value?.Json.ValueKind == JsonValueKind.True)
        {
            format = EDerivedIdFormat.Uuid8;
        }
        else
        {
            string unsupported = "Cluster.InspectionTargetUnavailable";
            if (response.Outcome != EOutcome.NotExecuted || !string.Equals(response.Reason, unsupported, StringComparison.Ordinal))
            {
                throw new IOException("Snapshot要求の生成方式に対応しているか確認できません。");
            }
            format = EDerivedIdFormat.Legacy;
        }
        if (formats.Count < 1024 || formats.ContainsKey(peer.PeerId))
        {
            formats[peer.PeerId] = (peer.InstanceId, format);
        }
        return format;
    }
}
