using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;

/// <summary>選択したクラスタの実測を有限件数・並列数で照会し、旧実体の返信を混ぜません。</summary>
internal sealed class DashboardTelemetry(ManagementApi api)
{
    private OverviewActivityWindow activity = new();
    private readonly List<OverviewPeer> peers = [];
    private bool followSelection = true;
    private readonly OverviewWorkflowNames workflowNames = new(api);

    /// <summary>この観測先で確認したWF表示名です。</summary>
    /// <returns>表示名または未取得時の識別子です。</returns>
    internal string WorkflowName(string id) => workflowNames.Name(id);

    /// <summary>単一・全クラスタ概要で共通に使う、一ページの有限Peer数です。</summary>
    internal static int MaximumPeers => 128;

    /// <summary>この観測画面が保持するクラスタです。</summary>
    internal string? ClusterId
    {
        get; private set;
    }

    /// <summary>概要の管理接続先です。</summary>
    internal string? SelectedPeerId
    {
        get; private set;
    }

    /// <summary>取得できた実体別の有限系列です。</summary>
    internal IReadOnlyList<OverviewPeer> Peers => peers.AsReadOnly();

    /// <summary>発見済みPeer数。取得範囲の省略を区別します。</summary>
    internal int KnownPeers
    {
        get; private set;
    }

    /// <summary>有限の取得対象ページです。</summary>
    internal int PeerOffset
    {
        get; private set;
    }

    /// <summary>残りのPeerも取得できるよう、固定数の次ページへ進みます。</summary>
    internal void NextPeers()
    {
        PeerOffset += MaximumPeers;
        if (KnownPeers <= PeerOffset)
        {
            PeerOffset = 0;
        }
        peers.Clear();
    }

    /// <summary>履歴一ページの欠測・省略を含む範囲表示です。</summary>
    internal int ActivityPeers
    {
        get; private set;
    }

    /// <summary>未取得・ページ省略・容量省略を全体件数と混同しません。</summary>
    internal bool ActivityLimited
    {
        get; private set;
    }

    /// <summary>直近ログの取得結果です。</summary>
    internal LogQueryResult? Logs
    {
        get; private set;
    }

    /// <summary>最後にログを取得した時刻です。</summary>
    internal DateTimeOffset? LogsReceivedAt
    {
        get; private set;
    }

    /// <summary>今回のログ取得不能理由です。</summary>
    internal string? LogProblem
    {
        get; private set;
    }

    /// <summary>現在時刻の60秒窓を表示します。</summary>
    /// <returns>原実行IDを重複除外した頻度です。</returns>
    internal IReadOnlyList<OverviewActivity> Activity(DateTimeOffset now) => activity.Read(now);

    /// <summary>別選択のデータを現在の表示へ使いません。</summary>
    /// <returns>同じクラスタと接続先を保持しているかです。</returns>
    internal bool IsCurrent => !followSelection || (string.Equals(ClusterId, api.SelectedClusterId, StringComparison.Ordinal) && string.Equals(SelectedPeerId, api.SelectedPeerId, StringComparison.Ordinal));

    /// <summary>同じ選択・実体だけを検証して画面用の状態へ反映します。</summary>
    /// <returns>一巡の読取り専用照会です。</returns>
    internal async Task RefreshAsync(ServiceStatus status, CancellationToken cancellationToken = default)
    {
        string? cluster = api.SelectedClusterId;
        string? selected = api.SelectedPeerId;
        if (cluster is null || selected is null || !string.Equals(selected, status.PeerId, StringComparison.Ordinal))
        {
            return;
        }
        followSelection = true;
        await RefreshTargetAsync(cluster, selected, status.InstanceId, status.Peers, true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>選択UIを変更せず、指定クラスタの指定実体だけを観測します。</summary>
    /// <returns>固定対象の一巡の結果です。</returns>
    internal async Task RefreshTargetAsync(string cluster, string selected, Guid selectedInstance, IReadOnlyList<PeerInfo> observedPeers, bool selectedConnection = false, IReadOnlyList<DashboardPeerSnapshot>? suppliedSnapshots = null, CancellationToken cancellationToken = default)
    {
        followSelection = selectedConnection;
        if (!string.Equals(ClusterId, cluster, StringComparison.Ordinal) || (selectedConnection && !string.Equals(SelectedPeerId, selected, StringComparison.Ordinal)))
        {
            peers.Clear();
            activity = new();
            Logs = null;
            LogsReceivedAt = null;
            PeerOffset = 0;
        }
        ClusterId = cluster;
        SelectedPeerId = selected;
        KnownPeers = observedPeers.Count;
        if (KnownPeers <= PeerOffset)
        {
            PeerOffset = 0;
        }
        int offset = PeerOffset;
        PeerInfo[] selectedPeers = observedPeers.OrderBy(peer => peer.HostId, StringComparer.Ordinal).ThenBy(peer => peer.PeerId, StringComparer.Ordinal).Skip(offset).Take(MaximumPeers).ToArray();
        using SemaphoreSlim concurrency = new(3, 3);
        Task<(OverviewPeer Peer, DashboardPeerSnapshot? Snapshot)>[] tasks = selectedPeers.Select(async peer =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ReadPeerAsync(cluster, peer, suppliedSnapshots, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        }).ToArray();
        (OverviewPeer Peer, DashboardPeerSnapshot? Snapshot)[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        if (!IsCurrent || offset != PeerOffset || !string.Equals(ClusterId, cluster, StringComparison.Ordinal) || !string.Equals(SelectedPeerId, selected, StringComparison.Ordinal))
        {
            return;
        }
        peers.Clear();
        peers.AddRange(results.Select(result => result.Peer));
        DateTimeOffset now = api.UtcNow;
        foreach ((OverviewPeer _, DashboardPeerSnapshot? snapshot) in results)
        {
            activity.Add(snapshot?.Executions?.Entries ?? [], now);
        }
        int covered = results.Count(result => result.Snapshot?.Executions is not null);
        bool limited = results.Any(result => result.Snapshot?.Executions?.Cursor is not null) || activity.CapacityLimited;
        ActivityPeers = covered;
        ActivityLimited = limited || covered < KnownPeers;
        DashboardPeerSnapshot? selectedSnapshot = results.FirstOrDefault(result => result.Peer.Peer.InstanceId == selectedInstance
            && string.Equals(result.Peer.Peer.PeerId, selected, StringComparison.Ordinal)).Snapshot;
        Logs = selectedSnapshot?.Logs;
        LogsReceivedAt = selectedSnapshot?.CheckedAt;
        LogProblem = Logs is null ? "The latest log read is unconfirmed"
                                  : null;
        await workflowNames.RefreshAsync(cluster, selected, activity.Read(now).Take(8).Select(row => row.WorkflowId)
            .Concat(Logs?.Entries.Select(entry => entry.WorkflowId).OfType<string>() ?? []), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>ClientHostが保持した履歴と実確認時刻を取得します。</summary>
    /// <returns>同じ実体の観測、または欠測状態です。</returns>
    private async Task<(OverviewPeer Peer, DashboardPeerSnapshot? Snapshot)> ReadPeerAsync(string cluster, PeerInfo peer, IReadOnlyList<DashboardPeerSnapshot>? suppliedSnapshots, CancellationToken cancellationToken = default)
    {
        OverviewPeer? previous = peers.FirstOrDefault(item => item.Peer.InstanceId == peer.InstanceId && string.Equals(item.Peer.PeerId, peer.PeerId, StringComparison.Ordinal));
        try
        {
            string path = "/client/dashboard?compact=true";
            if (previous?.Latest is { } latest)
            {
                path += "&after=" + latest.Id.ToString("D");
            }
            DashboardPeerSnapshot? snapshot;
            if (suppliedSnapshots is null)
            {
                snapshot = await api.GetForPeerAsync<DashboardPeerSnapshot>(path, peer.PeerId, cluster, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                snapshot = suppliedSnapshots.FirstOrDefault(item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal)
                    && string.Equals(item.Peer.PeerId, peer.PeerId, StringComparison.Ordinal) && item.Peer.InstanceId == peer.InstanceId);
            }
            if (snapshot is null || snapshot.Peer.InstanceId != peer.InstanceId || !string.Equals(snapshot.ClusterId, cluster, StringComparison.Ordinal)
                || !string.Equals(snapshot.Peer.PeerId, peer.PeerId, StringComparison.Ordinal))
            {
                return (new(peer, [], "Peer instance is unconfirmed"), null);
            }
            Contracts.Cluster.EPeerStatus? verified = null;
            Contracts.Cluster.HistorySynchronizationStatus? history = null;
            DateTimeOffset now = api.UtcNow;
            if (snapshot.Health is { } status && snapshot.CheckedAt is { } time && time <= now && now - time <= TimeSpan.FromSeconds(15))
            {
                PeerHealth? health = status.Deserialize<PeerHealth>(Contracts.Serialization.ContractJson.Options);
                if (health is not null && health.InstanceId == peer.InstanceId && string.Equals(health.PeerId, peer.PeerId, StringComparison.Ordinal)
                    && string.Equals(health.ClusterId, cluster, StringComparison.Ordinal))
                {
                    verified = health.Status;
                    history = health.HistorySynchronization;
                }
            }
            IReadOnlyList<ResourceSample> incoming = snapshot.Samples;
            if (snapshot.SampleSeries is { } series)
            {
                incoming = DashboardSampleSeries.Decode(series);
            }
            ResourceSample[] samples = DashboardSampleHistory.Merge(previous?.Samples ?? [], incoming, peer.InstanceId, now);

            // Core自身のLastSeenは受信通知で更新されないため、ClientHostが確認した同じ実体の観測時刻を使います。
            return (new(snapshot.Peer, samples, snapshot.Problem, verified, snapshot.CheckedAt, history), snapshot);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            return (new(peer, previous?.Samples ?? [], "The latest read failed"), null);
        }
    }
}
