using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;

/// <summary>PC表示の最後の一標本を永続化し、発見履歴の期限とは独立して保持します。</summary>
internal sealed class DashboardHostStore : IDisposable
{
    private readonly string path;
    private readonly Func<IReadOnlyList<DiscoveredCluster>> discover;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<(string Host, string Cluster, string Peer), DashboardHostPeer> saved = [];
    private readonly Dictionary<string, DateTimeOffset> removed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObservationAge> removalAges = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private bool loaded;
    private bool persistenceFailed;
    private ObservationAge? lastWrite;

    /// <summary>専用の表示保存先と、現在発見している実体の照合元を指定します。</summary>
    public DashboardHostStore(ClientHostOptions options, ObserverConnection connection) : this(options.StateDirectory, connection.GetClusters)
    {
    }

    /// <summary>同じ保存規則を独立したディレクトリで検証します。</summary>
    internal DashboardHostStore(string stateDirectory, Func<IReadOnlyList<DiscoveredCluster>> discover, TimeProvider? timeProvider = null)
    {
        Directory.CreateDirectory(stateDirectory);
        path = Path.Combine(stateDirectory, "dashboard-hosts.json");
        this.discover = discover;
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>毎回の実測はメモリーで更新し、実体変更・オフライン移行と最長一分間隔で保存します。</summary>
    /// <returns>確認済みの状態、または所有する処理の完了です。</returns>
    internal async Task ObserveAsync(IEnumerable<DashboardPeerSnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = clock.GetUtcNow();
            Dictionary<(string, string), PeerInfo> current = Current(now);
            bool changed = false;
            foreach (DashboardPeerSnapshot snapshot in snapshots.OrderByDescending(item => item.Peer.AgeAt(now)))
            {
                (string Host, string Cluster, string Peer) key = (snapshot.Peer.HostId, snapshot.ClusterId, snapshot.Peer.PeerId);
                bool online = current.TryGetValue((key.Cluster, key.Peer), out PeerInfo? peer) && peer.InstanceId == snapshot.Peer.InstanceId && string.Equals(peer.HostId, key.Host, StringComparison.Ordinal);
                if (removed.TryGetValue(snapshot.Peer.HostId, out DateTimeOffset deleted)
                    && (!online || !ObservedAfterRemoval(peer!, deleted)))
                {
                    continue;
                }
                if (online)
                {
                    removed.Remove(snapshot.Peer.HostId);
                    removalAges.Remove(snapshot.Peer.HostId);
                }
                saved.TryGetValue(key, out DashboardHostPeer? previous);
                changed |= previous is null || previous.Peer.InstanceId != snapshot.Peer.InstanceId || previous.Online != online;
                saved[key] = new(snapshot.ClusterId, snapshot.Peer, snapshot.Samples.Count switch { 0 => previous?.Sample, _ => snapshot.Samples[^1] }, online);
            }
            foreach ((var key, DashboardHostPeer value) in saved.ToArray())
            {
                bool online = current.TryGetValue((key.Cluster, key.Peer), out PeerInfo? peer) && peer.InstanceId == value.Peer.InstanceId && string.Equals(peer.HostId, value.Peer.HostId, StringComparison.Ordinal);
                changed |= online != value.Online;
                saved[key] = value with { Online = online };
            }
            if (changed || lastWrite is null || TimeSpan.FromMinutes(1) <= lastWrite.Value.Elapsed || persistenceFailed)
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
                lastWrite = new(clock);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>古い保存値を稼働中とせず、現在の発見情報で可用性を再判定します。</summary>
    /// <returns>確認済みの状態、または所有する処理の完了です。</returns>
    internal async Task<IReadOnlyList<DashboardHostPeer>> ReadAsync(string? cluster, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<(string, string), PeerInfo> current = Current(clock.GetUtcNow());
            if (persistenceFailed)
            {
                throw new IOException("Dashboard host history could not be saved.");
            }
            return saved.Values.Where(value => cluster is null || string.Equals(value.ClusterId, cluster, StringComparison.Ordinal))
                .Select(value => value with { Online = current.TryGetValue((value.ClusterId, value.Peer.PeerId), out PeerInfo? peer) && peer.InstanceId == value.Peer.InstanceId && string.Equals(peer.HostId, value.Peer.HostId, StringComparison.Ordinal) })
                .OrderBy(value => value.Peer.HostId, StringComparer.Ordinal).ThenBy(value => value.ClusterId, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>オフラインのPC表示だけを全クラスタから削除し、古い発見値で復活させません。</summary>
    /// <returns>確認済みの状態、または所有する処理の完了です。</returns>
    internal async Task<bool> RemoveAsync(string hostId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = clock.GetUtcNow();
            if (Current(now).Values.Any(peer => string.Equals(peer.HostId, hostId, StringComparison.Ordinal)))
            {
                return false;
            }
            DashboardHostPeer[] matches = saved.Values.Where(value => string.Equals(value.Peer.HostId, hostId, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0)
            {
                return false;
            }
            foreach (DashboardHostPeer value in matches)
            {
                saved.Remove((hostId, value.ClusterId, value.Peer.PeerId));
            }
            removed[hostId] = now;
            removalAges[hostId] = new(clock);
            bool committed = false;
            try
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    removed.Remove(hostId);
                    removalAges.Remove(hostId);
                    foreach (DashboardHostPeer value in matches)
                    {
                        saved[(hostId, value.ClusterId, value.Peer.PeerId)] = value;
                    }
                }
            }
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>削除後の新しい受信だけで表示を復元します。保存値には最終受信からの経過時間を残さず、再起動後は再受信を必要とします。</summary>
    /// <returns>削除より新しい生存確認があるかです。</returns>
    private bool ObservedAfterRemoval(PeerInfo peer, DateTimeOffset deleted)
    {
        if (peer.ObservationAge is { } age)
        {
            return !removalAges.TryGetValue(peer.HostId, out ObservationAge removal) || age.Elapsed < removal.Elapsed;
        }
        return deleted < peer.LastSeen;
    }

    /// <summary>最後の受信からの経過時間とUnavailableを確認し、PC単位の操作可否だけへ利用します。</summary>
    /// <returns>確認済みの状態、または所有する処理の完了です。</returns>
    private Dictionary<(string, string), PeerInfo> Current(DateTimeOffset now) => discover().SelectMany(cluster => cluster.Peers.Select(peer => (cluster.ClusterId, Peer: peer)))
        .Where(item => item.Peer.Status != EPeerStatus.Unavailable && TimeSpan.Zero <= item.Peer.AgeAt(now) && item.Peer.AgeAt(now) <= TimeSpan.FromSeconds(15))
        .GroupBy(item => (item.ClusterId, item.Peer.PeerId)).ToDictionary(group => group.Key, group => group.OrderBy(item => item.Peer.AgeAt(now)).First().Peer);

    /// <summary>保存済みの表示情報を一度だけ読みます。</summary>
    /// <returns>確認済みの状態、または所有する処理の完了です。</returns>
    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (loaded)
        {
            return;
        }
        if (File.Exists(path))
        {
            StoredHosts data = JsonSerializer.Deserialize<StoredHosts>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options) ?? throw new InvalidDataException("Dashboard host history is invalid.");
            foreach (DashboardHostPeer item in data.Hosts)
            {
                saved[(item.Peer.HostId, item.ClusterId, item.Peer.PeerId)] = item with { Online = false };
            }
            foreach ((string host, DateTimeOffset at) in data.Removed)
            {
                removed[host] = at;
            }
        }
        loaded = true;
    }

    /// <summary>既存保存を壊さない原子的置換を行います。</summary>
    /// <returns>確認済みの状態、または所有する処理の完了です。</returns>
    private async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        string temporary = path + ".pending";
        persistenceFailed = true;
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new StoredHosts(saved.Values.ToArray(), removed), ContractJson.Options), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, true);
        persistenceFailed = false;
    }

    /// <summary>保存の排他を解放します。</summary>
    public void Dispose() => gate.Dispose();

    /// <summary>クラスタ通信NICの観測を保存します。将来のWoL用物理NIC設定とは独立した表示情報です。</summary>
    private sealed record StoredHosts(IReadOnlyList<DashboardHostPeer> Hosts, IReadOnlyDictionary<string, DateTimeOffset> Removed);
}
