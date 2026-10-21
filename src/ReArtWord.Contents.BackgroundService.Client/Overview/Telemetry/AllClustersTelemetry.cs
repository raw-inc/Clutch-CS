using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;

/// <summary>発見したクラスタの件数を制限したページを、利用者の選択から独立して取得します。</summary>
internal sealed class AllClustersTelemetry(ManagementApi api)
{
    private readonly Dictionary<string, DashboardTelemetry> observations = new(StringComparer.Ordinal);
    private readonly List<DiscoveredCluster> clusters = [];
    private readonly List<(string Cluster, OverviewPeer Peer)> peers = [];
    private bool discoveryFailed;

    /// <summary>最終受信または短時間保持している発見状態です。</summary>
    internal IReadOnlyList<DiscoveredCluster> Clusters => clusters.AsReadOnly();

    /// <summary>可視範囲の実測です。クラスタとPeerの同名を混同しません。</summary>
    internal IReadOnlyList<(string Cluster, OverviewPeer Peer)> Peers => peers.AsReadOnly();

    /// <summary>活動とログはクラスタごとの一つの実装で保持します。</summary>
    internal IReadOnlyDictionary<string, DashboardTelemetry> Observations => observations;

    /// <summary>観測対象Peerの全件数です。</summary>
    internal int KnownPeers
    {
        get; private set;
    }

    /// <summary>可視の先頭位置です。</summary>
    internal int Offset
    {
        get; private set;
    }

    /// <summary>発見APIの取得状態です。</summary>
    internal string? Problem
    {
        get; private set;
    }

    /// <summary>実体が一致した今回の状態照会だけでクラスタの稼働確認を集計します。</summary>
    /// <returns>未照会・古い値をReadyへ丸めない全体状態と確認範囲です。</returns>
    internal (string State, int Ready, int Verified, DateTimeOffset? CheckedAt, HistorySynchronizationStatus? History) Health(DiscoveredCluster cluster, DateTimeOffset now)
    {
        OverviewPeer[] verified = peers.Where(item => string.Equals(item.Cluster, cluster.ClusterId, StringComparison.Ordinal))
            .Select(item => item.Peer).Where(peer => peer.VerifiedStatus is not null && peer.StatusCheckedAt is { } time
                && time <= now && now - time <= TimeSpan.FromSeconds(15)
                && cluster.Peers.Any(current => current.InstanceId == peer.Peer.InstanceId && string.Equals(current.PeerId, peer.Peer.PeerId, StringComparison.Ordinal)))
            .ToArray();
        int ready = verified.Count(peer => peer.VerifiedStatus == EPeerStatus.Ready);
        string state = "Unconfirmed";
        if (!discoveryFailed && cluster.LastSeen <= now && now - cluster.LastSeen <= TimeSpan.FromSeconds(15))
        {
            if ((0 < verified.Length && ready != verified.Length) || 1 < cluster.CoordinatorCandidates.Count)
            {
                state = "Needs attention";
            }
            else
            {
                if (0 < ready && ready == cluster.Peers.Count && cluster.CoordinatorCandidates.Count == 1)
                {
                    state = "Ready";
                }
            }
        }
        HistorySynchronizationStatus[] background = verified.Where(peer => peer.VerifiedStatus == EPeerStatus.Ready)
            .Select(peer => peer.HistorySynchronization).OfType<HistorySynchronizationStatus>().Where(history => 0 < history.Total).ToArray();
        HistorySynchronizationStatus? history = null;
        if (background.Any(item => 0 < item.Pending))
        {
            history = new(background.Sum(item => item.Pending), background.Select(item => item.Problem).FirstOrDefault(problem => problem is not null), background.Sum(item => item.Total));
        }
        return (state, ready, verified.Length, verified.Select(peer => peer.StatusCheckedAt).DefaultIfEmpty().Max(), history);
    }

    /// <summary>次の件数を制限したページへ移ります。</summary>
    internal void NextPage()
    {
        Offset += DashboardTelemetry.MaximumPeers;
        if (KnownPeers <= Offset)
        {
            Offset = 0;
        }
        peers.Clear();
    }

    /// <summary>発見と可視ページだけを取得し、外れた観測器を解放します。</summary>
    /// <returns>一巡の読取りの完了です。</returns>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            IReadOnlyList<DiscoveredCluster> discovered = await api.GetAsync<List<DiscoveredCluster>>("/client/clusters", timeout.Token).ConfigureAwait(false) ?? [];
            discovered = discovered.Select(current =>
            {
                DiscoveredCluster? previous = clusters.FirstOrDefault(old => string.Equals(old.ClusterId, current.ClusterId, StringComparison.Ordinal));
                PeerInfo[] lost = (previous?.Peers ?? []).Where(old => api.UtcNow - old.LastSeen < TimeSpan.FromMinutes(1)
                    && !current.Peers.Any(peer => string.Equals(peer.PeerId, old.PeerId, StringComparison.Ordinal)))
                    .Select(old => old with { Status = EPeerStatus.Unavailable }).ToArray();
                return current with { Peers = current.Peers.Concat(lost).ToArray() };
            }).ToArray();
            DiscoveredCluster[] missing = clusters.Where(old => !discovered.Any(current => string.Equals(current.ClusterId, old.ClusterId, StringComparison.Ordinal)) && api.UtcNow - old.LastSeen < TimeSpan.FromMinutes(1))
                .Select(old => old with { Peers = old.Peers.Select(peer => peer with { Status = EPeerStatus.Unavailable }).ToArray(), CoordinatorCandidates = [] }).ToArray();
            clusters.Clear();
            clusters.AddRange(discovered.Concat(missing).OrderBy(cluster => cluster.ClusterId, StringComparer.Ordinal));
            Problem = null;
            discoveryFailed = false;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            discoveryFailed = true;
            Problem = "Cluster discovery is unconfirmed";
            if (exception is HttpRequestException { StatusCode: null })
            {
                Problem = "The local Dashboard host is unavailable. Start ClientHost on this PC to restore cluster discovery. A cached page alone cannot monitor peers.";
            }
        }
        (string Cluster, PeerInfo Peer)[] targets = clusters.SelectMany(cluster => cluster.Peers.Select(peer => (Cluster: cluster.ClusterId, Peer: peer)))
            .OrderBy(item => item.Peer.HostId, StringComparer.Ordinal).ThenBy(item => item.Cluster, StringComparer.Ordinal).ThenBy(item => item.Peer.PeerId, StringComparer.Ordinal).ToArray();
        KnownPeers = targets.Length;
        if (KnownPeers <= Offset)
        {
            Offset = 0;
        }
        IGrouping<string, (string Cluster, PeerInfo Peer)>[] page = targets.Skip(Offset).Take(DashboardTelemetry.MaximumPeers).GroupBy(item => item.Cluster, StringComparer.Ordinal).ToArray();
        foreach (string key in observations.Keys.Where(key => !page.Any(group => string.Equals(group.Key, key, StringComparison.Ordinal))).ToArray())
        {
            observations.Remove(key);
        }
        foreach (IGrouping<string, (string Cluster, PeerInfo Peer)> group in page)
        {
            observations.TryAdd(group.Key, new DashboardTelemetry(api));
        }
        DashboardSnapshotQuery[] queries = page.SelectMany(group => group).Select(target => new DashboardSnapshotQuery(target.Cluster,
            target.Peer.PeerId, target.Peer.InstanceId, observations[target.Cluster].Peers.FirstOrDefault(previous => previous.Peer.InstanceId == target.Peer.InstanceId)?.Latest?.Id)).ToArray();
        IReadOnlyList<DashboardPeerSnapshot> snapshots = await ReadSnapshotsAsync(queries, cancellationToken).ConfigureAwait(false);
        using SemaphoreSlim concurrency = new(3, 3);
        await Task.WhenAll(page.Select(async group =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DashboardTelemetry telemetry = observations[group.Key];
                PeerInfo[] selected = group.Select(item => item.Peer).ToArray();
                PeerInfo first = selected.FirstOrDefault(peer => string.Equals(peer.PeerId, telemetry.SelectedPeerId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(peer.Status))
                    ?? selected.OrderByDescending(peer => PeerStatusRules.CanParticipate(peer.Status)).ThenByDescending(peer => peer.LastSeen).First();
                await telemetry.RefreshTargetAsync(group.Key, first.PeerId, first.InstanceId, selected, suppliedSnapshots: snapshots, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        })).ConfigureAwait(false);
        peers.Clear();
        peers.AddRange(observations.SelectMany(pair => pair.Value.Peers.Select(peer => (pair.Key, peer))));
    }

    /// <summary>初回の121点履歴も小さい組で取得し、一組の失敗を他の実体へ波及させません。</summary>
    /// <returns>同時要求数と要求ごとの期限を制限した、成功した組の観測です。</returns>
    private async Task<IReadOnlyList<DashboardPeerSnapshot>> ReadSnapshotsAsync(DashboardSnapshotQuery[] queries, CancellationToken cancellationToken = default)
    {
        using SemaphoreSlim concurrency = new(3, 3);
        Task<IReadOnlyList<DashboardPeerSnapshot>>[] reads = queries.Chunk(4).Select(async chunk =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                return (IReadOnlyList<DashboardPeerSnapshot>)(await api.PostAsync<List<DashboardPeerSnapshot>>("/client/dashboard/batch?compact=true", chunk, timeout.Token).ConfigureAwait(false) ?? []);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Problem = "The latest read failed";
                return Array.Empty<DashboardPeerSnapshot>();
            }
            finally
            {
                concurrency.Release();
            }
        }).ToArray();
        IReadOnlyList<DashboardPeerSnapshot>[] snapshots = await Task.WhenAll(reads).ConfigureAwait(false);
        return snapshots.SelectMany(chunk => chunk).ToArray();
    }
}
