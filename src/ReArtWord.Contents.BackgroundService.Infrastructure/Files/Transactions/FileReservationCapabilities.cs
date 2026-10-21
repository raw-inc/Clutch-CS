using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

// LEGACY-FORMAT: file-reservation-list-v1 — 全Peerの更新確認後に旧一覧との混在判定を撤去します。
/// <summary>新しい台帳形式を理解する起動実体を確認し、更新中の旧版には従来形式を維持します。</summary>
internal sealed class FileReservationCapabilities(PeerCluster cluster)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, (Guid Instance, bool Supported)> observations = new(StringComparer.Ordinal);

    internal static string Kind => "file.gate.slots.v1";

    /// <summary>未観測・離脱中の既知Peerを、更新済みと推測しません。</summary>
    /// <returns>全既知Peerの対応と、明示的な旧版の有無です。</returns>
    internal async Task<(bool All, bool Legacy)> CheckAsync(CancellationToken cancellationToken = default)
    {
        bool all = true, legacy = false;
        foreach (PeerInfo peer in cluster.GetPeers())
        {
            bool? supported = await SupportsAsync(peer, cancellationToken).ConfigureAwait(false);
            all &= supported == true;
            legacy |= supported == false;
        }
        return (all, legacy);
    }

    /// <summary>再起動時には新実体へ照会し直し、通信欠測と未対応を区別します。</summary>
    /// <returns>対応、未対応、または未確認です。</returns>
    internal async Task<bool?> SupportsAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        if (peer.InstanceId == cluster.LocalInstanceId)
        {
            return true;
        }
        using (gate.EnterScope())
        {
            if (observations.TryGetValue(peer.PeerId, out (Guid Instance, bool Supported) known) && known.Instance == peer.InstanceId)
            {
                return known.Supported;
            }
        }
        if (!PeerStatusRules.CanParticipate(peer.Status))
        {
            return null;
        }
        ClusterResponse response = await cluster.InspectAsync(new(Guid.NewGuid(), Kind, peer.PeerId, peer.InstanceId,
            DateTimeOffset.UtcNow.AddSeconds(30), NodeValue.Null), cancellationToken).ConfigureAwait(false);
        bool? supported = null;
        string unsupported = "Cluster.InspectionTargetUnavailable";
        if (response.Outcome == EOutcome.Succeeded && response.Value?.Json.ValueKind == JsonValueKind.True)
        {
            supported = true;
        }
        if (response.Outcome == EOutcome.NotExecuted && string.Equals(response.Reason, unsupported, StringComparison.Ordinal))
        {
            supported = false;
        }
        if (supported is bool confirmed)
        {
            using (gate.EnterScope())
            {
                if (observations.ContainsKey(peer.PeerId) || observations.Count < 1024)
                {
                    observations[peer.PeerId] = (peer.InstanceId, confirmed);
                }
            }
        }
        return supported;
    }
}
