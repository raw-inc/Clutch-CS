using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;

/// <summary>起動実体ごとに準備要求の圧縮対応を読み取り専用で確認し、未確認対象へ新形式を送りません。</summary>
internal sealed class ReplicationPreparationCapabilities(PeerCluster cluster)
{
    private readonly ConcurrentDictionary<string, Observation> observations = new(StringComparer.Ordinal);

    /// <summary>旧版の明示的未対応は記憶し、通信欠測は通常形式を選んで次回再確認します。</summary>
    /// <returns>この実体が展開できる最大バイト数。未確認・未対応なら0です。</returns>
    internal async Task<int> MaximumBytesAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        if (observations.TryGetValue(peer.PeerId, out Observation? known) && known.InstanceId == peer.InstanceId)
        {
            return known.MaximumBytes;
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        ClusterRequest request = new(Guid.NewGuid(), ReplicationPreparationPayload.Capability, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(2), NodeValue.Null);
        try
        {
            WireMessage? reply = await cluster.ExchangeAsync("inspect", peer.PeerId, peer.InstanceId, JsonSerializer.SerializeToElement(request), timeout.Token).ConfigureAwait(false);
            ClusterResponse? response = reply?.Body.Deserialize<ClusterResponse>();
            int? maximum = null;
            if (response?.Outcome == EOutcome.Succeeded && response.Value?.Json.ValueKind == JsonValueKind.Number && response.Value.Json.TryGetInt32(out int supported) && 0 < supported)
            {
                maximum = supported;
            }
            string unavailable = "Cluster.InspectionTargetUnavailable";
            if (response?.Outcome == EOutcome.NotExecuted && string.Equals(response.Reason, unavailable, StringComparison.Ordinal))
            {
                maximum = 0;
            }
            if (maximum is { } value)
            {
                if (observations.Count < 1024 || observations.ContainsKey(peer.PeerId))
                {
                    observations[peer.PeerId] = new(peer.InstanceId, value);
                }
                return value;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 参照だけの欠測では書込みは始まっていないため、従来形式を選べます。
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or JsonException)
        {
            // 保存要求をまだ送っていない照会障害だけを従来形式へ戻します。
        }
        return 0;
    }

    /// <summary>大きな本文を持たない、固定実体の能力確認です。</summary>
    private sealed record Observation(Guid InstanceId, int MaximumBytes);
}
