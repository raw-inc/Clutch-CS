using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

/// <summary>通常参加や共有状態同期をせず、受信した存在情報だけを有限保持します。</summary>
internal sealed class ObserverCatalog(TimeProvider clock, TimeSpan lifetime)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Observation> peers = new(StringComparer.Ordinal);

    /// <summary>送信実体とannouncementの整合を確認して記録します。</summary>
    public void Observe(WireMessage message, PeerAnnouncement announcement, string address)
    {
        if (string.IsNullOrWhiteSpace(message.ClusterId) || announcement.Peer.InstanceId != message.InstanceId
            || !string.Equals(announcement.Peer.PeerId, message.SenderId, StringComparison.Ordinal))
        {
            return;
        }
        using (gate.EnterScope())
        {
            string key = string.Concat(message.ClusterId, "\0", message.SenderId, "\0", message.InstanceId.ToString("N"));
            peers.TryGetValue(key, out Observation? current);
            if (!announcement.IsNewerThan(current?.Announcement))
            {
                return;
            }
            if (current is null && (1024 <= peers.Count || 64 <= peers.Values.Select(item => item.ClusterId).Distinct(StringComparer.Ordinal).Count()))
            {
                Expire();
            }
            if (!peers.ContainsKey(key) && (1024 <= peers.Count || (64 <= peers.Values.Select(item => item.ClusterId).Distinct(StringComparer.Ordinal).Count()
                && !peers.Values.Any(item => string.Equals(item.ClusterId, message.ClusterId, StringComparison.Ordinal)))))
            {
                return;
            }
            DateTimeOffset received = clock.GetUtcNow();
            ObservationAge age = new(clock);
            peers[key] = new(message.ClusterId, announcement with { Peer = announcement.Peer with { Address = address, LastSeen = received, ObservationAge = age } }, received, age);
        }
    }

    /// <summary>古い情報を通常参加可能として延長せずクラスタ別に返します。</summary>
    /// <returns>現在発見しているクラスタ一覧です。</returns>
    public IReadOnlyList<DiscoveredCluster> Snapshot()
    {
        using (gate.EnterScope())
        {
            return peers.Values.Where(item => item.Age.Elapsed < lifetime).GroupBy(item => item.ClusterId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new DiscoveredCluster(group.Key, group.Select(item => item.Announcement.Peer).OrderBy(peer => peer.PeerId, StringComparer.Ordinal).ToArray(),
                    group.Select(item => item.Announcement.Coordinator).Where(id => id is not null).Select(id => id!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    group.MinBy(item => item.Age.Elapsed)!.Received)).ToArray();
        }
    }

    /// <summary>表示期限後も番号を保持し、容量確保が必要な時だけ古い観測を除きます。</summary>
    private void Expire()
    {
        foreach (string key in peers.Where(pair => lifetime <= pair.Value.Age.Elapsed).Select(pair => pair.Key).ToArray())
        {
            peers.Remove(key);
        }
    }

    /// <summary>表示用の受信日時と、有効期間を測る単調時計を分けて保持します。</summary>
    private sealed record Observation(string ClusterId, PeerAnnouncement Announcement, DateTimeOffset Received, ObservationAge Age);
}
