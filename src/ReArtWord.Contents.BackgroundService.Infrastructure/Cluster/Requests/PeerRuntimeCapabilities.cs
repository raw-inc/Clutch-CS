using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>旧版が読む通知形式を維持し、新方式への対応だけを起動実体ごとに一度確認します。</summary>
internal sealed class PeerRuntimeCapabilities(PeerCluster cluster)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Observation> observations = new(StringComparer.Ordinal);
    private volatile bool enabled;

    /// <summary>新方式のhandlerを登録したCoreだけが照会を行います。</summary>
    internal void Enable(PeerMembership membership)
    {
        using (gate.EnterScope())
        {
            if (enabled)
            {
                return;
            }
            cluster.RegisterInspection(Kind, (_, _) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(true))));
            membership.EnableBufferedRuntime();
            enabled = true;
        }
    }

    /// <summary>副作用を持たない、旧版では明示的に未対応となる照会名です。</summary>
    internal static string Kind => "runtime.capabilities";

    /// <summary>対応情報の確認前に通常参加させず、同じ実体の通知では保存や追加通信をしません。</summary>
    /// <returns>確認済み情報を付けた参加通知。照会欠測では次の通知で再確認します。</returns>
    internal async Task<PeerAnnouncement?> ResolveAsync(WireMessage message, CancellationToken cancellationToken = default)
    {
        PeerAnnouncement? announcement = message.Body.Deserialize<PeerAnnouncement>();
        if (announcement is null || announcement.Peer.InstanceId != message.InstanceId || !string.Equals(announcement.Peer.PeerId, message.SenderId, StringComparison.Ordinal))
        {
            return null;
        }
        PeerInfo peer = announcement.Peer;
        if (!enabled || peer.InstanceId == cluster.LocalInstanceId)
        {
            return announcement;
        }
        Observation observation;
        using (gate.EnterScope())
        {
            if (!observations.TryGetValue(peer.PeerId, out Observation? current) || current.InstanceId != peer.InstanceId)
            {
                if (current is null && 1024 <= observations.Count)
                {
                    return null;
                }
                current = new(peer.InstanceId, new(() => ProbeAsync(peer, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
                observations[peer.PeerId] = current;
            }
            observation = current;
        }
        bool? supported = await observation.Completion.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (supported is null)
        {
            using (gate.EnterScope())
            {
                if (observations.GetValueOrDefault(peer.PeerId) == observation)
                {
                    observations.Remove(peer.PeerId);
                }
            }
            return null;
        }
        return announcement with { Peer = peer with { BufferedRuntime = supported.Value } };
    }

    /// <summary>固定実体の参照専用処理を使用し、未対応と通信欠測を区別します。</summary>
    /// <returns>確認済みの対応可否、または未確認のnullです。</returns>
    private async Task<bool?> ProbeAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        ClusterRequest request = new(Guid.NewGuid(), Kind, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(30), NodeValue.Null);
        try
        {
            WireMessage? reply = await cluster.ExchangeAsync("inspect", peer.PeerId, peer.InstanceId, JsonSerializer.SerializeToElement(request), cancellationToken).ConfigureAwait(false);
            ClusterResponse? response = reply?.Body.Deserialize<ClusterResponse>();
            if (response?.Outcome == EOutcome.Succeeded && response.Value?.Json.ValueKind == JsonValueKind.True)
            {
                return true;
            }
            string unavailable = "Cluster.InspectionTargetUnavailable";
            if (response?.Outcome == EOutcome.NotExecuted && string.Equals(response.Reason, unavailable, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or JsonException)
        {
            // 次の生存通知で同じ実体へ参照だけを行います。実行要求は作成しません。
        }
        return null;
    }

    /// <summary>一Peerの現在実体に対応する、本文を保持しない一度だけの照会です。</summary>
    private sealed record Observation(Guid InstanceId, Lazy<Task<bool?>> Completion);
}
