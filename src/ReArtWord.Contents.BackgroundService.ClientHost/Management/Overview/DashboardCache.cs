using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;

/// <summary>画面寿命から独立して観測し、実体・件数・時間の境界でメモリーを制限します。</summary>
internal sealed class DashboardCache : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly Func<IReadOnlyList<DiscoveredCluster>> discover;
    private readonly Func<ManagementCommand, CancellationToken, Task<JsonElement?>> inspect;
    private readonly ConcurrentDictionary<(string Cluster, string Peer, Guid Instance), DashboardPeerSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<(string Cluster, string Peer, Guid Instance), DateTimeOffset> legacyUntil = new();
    private readonly DashboardHostStore? hosts;

    /// <summary>共有Observerを借用し、参照専用の照会を実体に固定します。</summary>
    public DashboardCache(ObserverConnection connection, DashboardHostStore? hosts = null) : this(connection.GetClusters, async (command, token) =>
    {
        UdpObserver observer = connection.FindObserver(command.ClusterId, command.TargetPeerId) ?? throw new InvalidOperationException("Peer route is unavailable.");
        ManagementReply reply = await observer.InspectAsync(command, token).ConfigureAwait(false);
        return reply.Outcome == EOutcome.Succeeded ? reply.Value?.Json
                                                   : null;
    })
    {
        this.hosts = hosts;
    }

    /// <summary>発見と読取りの通信境界を指定します。</summary>
    internal DashboardCache(Func<IReadOnlyList<DiscoveredCluster>> discover, Func<ManagementCommand, CancellationToken, Task<JsonElement?>> inspect)
    {
        this.discover = discover;
        this.inspect = inspect;
    }

    /// <summary>発見中の同じ実体だけを返し、古い状態をReadyの証明にしません。</summary>
    /// <returns>保持中の観測と、既受信点以降の履歴です。点が無ければ全履歴です。</returns>
    public DashboardPeerSnapshot? Read(string cluster, string peer, Guid? after = null, bool healthOnly = false, bool compact = false)
    {
        PeerInfo[] current = discover().Where(item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal))
            .SelectMany(item => item.Peers).Where(item => string.Equals(item.PeerId, peer, StringComparison.Ordinal)).ToArray();
        if (current.Length != 1 || !snapshots.TryGetValue((cluster, peer, current[0].InstanceId), out DashboardPeerSnapshot? snapshot))
        {
            return null;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (snapshot.CheckedAt is null || now < snapshot.CheckedAt || TimeSpan.FromSeconds(15) < now - snapshot.CheckedAt)
        {
            snapshot = snapshot with { Status = null, Health = null, Problem = "The latest read is unconfirmed" };
        }
        if (healthOnly)
        {
            return snapshot with { Peer = current[0], Status = null, Samples = [], Executions = null, Logs = null, NodeExecutions = null };
        }
        ResourceSample[] samples = snapshot.Samples.Where(sample => now.AddMinutes(-6) <= sample.ObservedAt).ToArray();
        int index = Array.FindIndex(samples, sample => sample.Id == after);
        ResourceSample[] pending = samples.Skip(index + 1).ToArray();
        if (compact && 1 < pending.Length)
        {
            IReadOnlyList<DashboardSampleSeries> series = DashboardSampleSeries.Encode(pending);
            if (series.Count * 2 < pending.Length)
            {
                return snapshot with { Samples = [], SampleSeries = series, NodeExecutions = null };
            }
        }
        return snapshot with { Samples = pending, NodeExecutions = null };
    }

    /// <summary>画面の一巡を一往復で返し、追加のCore照会や無制限な本文を生成しません。</summary>
    /// <returns>要求された同じ実体のキャッシュだけです。</returns>
    internal IReadOnlyList<DashboardPeerSnapshot> ReadMany(IReadOnlyList<DashboardSnapshotQuery> queries, bool compact = false)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (128 < queries.Count || queries.Any(query => query is null || query.InstanceId == Guid.Empty)
            || queries.DistinctBy(query => (query.ClusterId, query.PeerId, query.InstanceId)).Count() != queries.Count)
        {
            throw new ArgumentException("Select up to 128 unique observed instances.", nameof(queries));
        }
        List<DashboardPeerSnapshot> result = [];
        foreach (DashboardSnapshotQuery query in queries)
        {
            if (Read(query.ClusterId, query.PeerId, query.After, query.HealthOnly, compact && !query.DiagnosticsOnly) is { } snapshot && snapshot.Peer.InstanceId == query.InstanceId)
            {
                if (query.DiagnosticsOnly && !query.HealthOnly && snapshots.TryGetValue((query.ClusterId, query.PeerId, query.InstanceId), out DashboardPeerSnapshot? diagnostic))
                {
                    snapshot = snapshot with { Samples = [], Logs = null, Health = null, Executions = null, NodeExecutions = diagnostic.NodeExecutions };
                }
                result.Add(snapshot);
            }
        }
        return result;
    }

    /// <summary>稼働・計測の3秒周期を、ログ・履歴の10秒周期と照会待ちから分離します。</summary>
    /// <returns>所有する両監視処理が停止するまでのTaskです。</returns>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(PollAsync(false, stoppingToken), PollAsync(true, stoppingToken));

    /// <summary>照会種別ごとに並列数と期限を制限し、消失した実体の保持を解放します。</summary>
    /// <returns>Host停止までの監視処理です。</returns>
    private async Task PollAsync(bool details, CancellationToken cancellationToken = default)
    {
        TimeSpan interval = TimeSpan.FromSeconds(3);
        if (details)
        {
            interval = TimeSpan.FromSeconds(10);
        }
        using PeriodicTimer timer = new(interval);
        do
        {
            (string Cluster, PeerInfo Peer)[] targets = discover()
                .SelectMany(cluster => cluster.Peers.Select(peer => (Cluster: cluster.ClusterId, Peer: peer))).Take(256).ToArray();
            foreach ((string Cluster, string Peer, Guid Instance) key in snapshots.Keys)
            {
                if (!targets.Any(target => string.Equals(target.Cluster, key.Cluster, StringComparison.Ordinal)
                    && string.Equals(target.Peer.PeerId, key.Peer, StringComparison.Ordinal) && target.Peer.InstanceId == key.Instance))
                {
                    snapshots.TryRemove(key, out _);
                    legacyUntil.TryRemove(key, out _);
                }
            }
            await Parallel.ForEachAsync(targets, new ParallelOptions
            {
                MaxDegreeOfParallelism = details ? 2
                                                 : 8,
                CancellationToken = cancellationToken
            }, async (target, token) => await RefreshAsync(target.Cluster, target.Peer, details, token).ConfigureAwait(false)).ConfigureAwait(false);
            if (!details && hosts is not null)
            {
                try
                {
                    await hosts.ObserveAsync(snapshots.Values, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    // 表示履歴の保存障害で現在の計測を停止しません。保存失敗は履歴APIでもエラーとして通知します。
                }
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>診断取得の失敗を稼働確認へ波及させず、同じ実体の観測だけを更新します。</summary>
    /// <returns>一実体の観測完了です。</returns>
    // 抑制理由: 開始した照会Taskは全てWhenAllで完了を確認し、待機ハンドルを作成しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "所有する全照会TaskをWhenAllでawaitします。")]
    internal async Task RefreshAsync(string cluster, PeerInfo peer, bool details, CancellationToken cancellationToken = default)
    {
        (string, string, Guid) key = (cluster, peer.PeerId, peer.InstanceId);
        DashboardPeerSnapshot empty = new(cluster, peer, null, null, null, []);
        if (details)
        {
            Task<JsonElement?> executions = InspectAsync(cluster, peer, "executions.list", new ExecutionHistoryQuery(100, MaximumBytes: 65536), cancellationToken);
            Task<JsonElement?> logs = InspectAsync(cluster, peer, "logs.query", new LogQuery(From: DateTimeOffset.UtcNow.AddMinutes(-15), Limit: 8, MaximumBytes: 32768), cancellationToken);
            await Task.WhenAll(executions, logs).ConfigureAwait(false);
            ExecutionHistoryPage? history = Deserialize<ExecutionHistoryPage>(await executions.ConfigureAwait(false));
            ExecutionHistoryPage? completions = await ReadActivityAsync(cluster, peer, cancellationToken).ConfigureAwait(false) ?? history;
            LogQueryResult? recent = Deserialize<LogQueryResult>(await logs.ConfigureAwait(false));
            snapshots.AddOrUpdate(key, empty with { Executions = DashboardHistory.Activity(null, completions, DateTimeOffset.UtcNow), Logs = recent, NodeExecutions = DashboardHistory.NodeResults(null, history, peer.PeerId, DateTimeOffset.UtcNow) },
                (_, previous) => previous with { Executions = DashboardHistory.Activity(previous.Executions, completions, DateTimeOffset.UtcNow), Logs = recent, NodeExecutions = DashboardHistory.NodeResults(previous.NodeExecutions, history, peer.PeerId, DateTimeOffset.UtcNow) });
            return;
        }
        (JsonElement? health, JsonElement? status, JsonElement? resource) = await ReadCurrentAsync(cluster, peer, cancellationToken).ConfigureAwait(false);
        JsonElement? confirmedHealth = Validate(health, cluster, peer);
        JsonElement? confirmedStatus = Validate(status, cluster, peer);
        ResourceSample? sample = Deserialize<ResourceSample>(resource);
        ResourceSample[] incoming = [];
        if (sample?.InstanceId == peer.InstanceId && string.Equals(sample.PeerId, peer.PeerId, StringComparison.Ordinal))
        {
            incoming = [DashboardSampleHistory.Project(sample)];
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        snapshots.AddOrUpdate(key, _ => Updated(empty), (_, previous) => Updated(previous));

        DashboardPeerSnapshot Updated(DashboardPeerSnapshot previous) => previous with
        {
            Peer = peer,
            CheckedAt = confirmedHealth is null ? null
                                                : now,
            Health = confirmedHealth,
            Status = confirmedStatus,
            Samples = DashboardSampleHistory.Merge(previous.Samples, incoming, peer.InstanceId, now),
            Problem = incoming.Length == 0 ? "The latest read is unconfirmed"
                                           : null
        };
    }

    /// <summary>活動表示は完了順の要約を辿り、長時間実行や大きい出力で取得範囲を狭めません。</summary>
    /// <returns>60秒窓の最大2000件です。未対応時はnull、途中で止まった場合は続き位置を残します。</returns>
    private async Task<ExecutionHistoryPage?> ReadActivityAsync(string cluster, PeerInfo peer, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        DateTimeOffset from = DateTimeOffset.UtcNow.AddMinutes(-1);
        List<ExecutionResultView> entries = [];
        string? cursor = null;
        do
        {
            ExecutionHistoryPage? page = Deserialize<ExecutionHistoryPage>(await InspectAsync(cluster, peer, "executions.activity",
                new ExecutionHistoryQuery(100, cursor, 65536), timeout.Token).ConfigureAwait(false));
            if (page is null)
            {
                return cursor is null ? null
                                      : new(entries, cursor);
            }
            foreach (ExecutionResultView entry in page.Entries)
            {
                if ((entry.Report?.CompletedAt ?? entry.Summary?.CompletedAt) is { } completed && completed < from)
                {
                    return new(entries, null);
                }
                entries.Add(entry);
            }
            if (page.Cursor is not null && string.Equals(cursor, page.Cursor, StringComparison.Ordinal))
            {
                return new(entries, cursor);
            }
            cursor = page.Cursor;
        }
        while (cursor is not null && entries.Count < 2000 && !timeout.IsCancellationRequested);
        return new(entries, cursor);
    }

    /// <summary>新Coreでは一往復にまとめ、旧Coreや欠測時は既存の独立照会を維持します。</summary>
    /// <returns>確認した応答だけです。失敗した応答を成功へ置き換えません。</returns>
    // 抑制理由: 開始した照会TaskはWhenAllで完了を確認し、待機ハンドルを作成しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "所有する全照会TaskをWhenAllでawaitします。")]
    private async Task<(JsonElement? Health, JsonElement? Status, JsonElement? Resource)> ReadCurrentAsync(string cluster, PeerInfo peer, CancellationToken cancellationToken = default)
    {
        (string, string, Guid) key = (cluster, peer.PeerId, peer.InstanceId);
        if (!legacyUntil.TryGetValue(key, out DateTimeOffset next) || next <= DateTimeOffset.UtcNow)
        {
            JsonElement? bundle = await InspectAsync(cluster, peer, "dashboard.current", null, cancellationToken).ConfigureAwait(false);
            string healthKey = "health", statusKey = "status", resourceKey = "resource";
            if (bundle is { ValueKind: JsonValueKind.Object } current && current.TryGetProperty(healthKey, out JsonElement health)
                && current.TryGetProperty(statusKey, out JsonElement status) && current.TryGetProperty(resourceKey, out JsonElement resource))
            {
                legacyUntil.TryRemove(key, out _);
                return (health, status, resource);
            }
            legacyUntil[key] = DateTimeOffset.UtcNow.AddMinutes(1);
        }
        Task<JsonElement?> healthRead = InspectAsync(cluster, peer, "health", null, cancellationToken);
        Task<JsonElement?> statusRead = InspectAsync(cluster, peer, "status", null, cancellationToken);
        Task<JsonElement?> resourceRead = InspectAsync(cluster, peer, "resources.latest", new ResourceSampleQuery(Summary: true), cancellationToken);
        await Task.WhenAll(healthRead, statusRead, resourceRead).ConfigureAwait(false);
        return (await healthRead.ConfigureAwait(false), await statusRead.ConfigureAwait(false), await resourceRead.ConfigureAwait(false));
    }

    /// <summary>照会ごとの期限と障害境界を設け、他の成功した照会を破棄しません。</summary>
    /// <returns>成功した内容だけです。</returns>
    private async Task<JsonElement?> InspectAsync(string cluster, PeerInfo peer, string operation, object? payload, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            NodeValue? body = payload is null ? null
                                              : NodeValue.FromJson(JsonSerializer.SerializeToElement(payload, ContractJson.Options));
            return await inspect(new(Guid.NewGuid(), cluster, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(2), operation, body), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>古い実体や別対象の状態を稼働確認へ混入させません。</summary>
    /// <returns>対象が全て一致した状態です。</returns>
    private static JsonElement? Validate(JsonElement? value, string cluster, PeerInfo peer)
    {
        string instanceProperty = "instanceId", peerProperty = "peerId", clusterProperty = "clusterId";
        if (value is { ValueKind: JsonValueKind.Object } json
            && json.TryGetProperty(instanceProperty, out JsonElement instance) && instance.ValueKind == JsonValueKind.String && instance.TryGetGuid(out Guid id) && id == peer.InstanceId
            && json.TryGetProperty(peerProperty, out JsonElement target) && target.ValueKind == JsonValueKind.String && string.Equals(target.GetString(), peer.PeerId, StringComparison.Ordinal)
            && json.TryGetProperty(clusterProperty, out JsonElement scope) && scope.ValueKind == JsonValueKind.String && string.Equals(scope.GetString(), cluster, StringComparison.Ordinal))
        {
            return json;
        }
        return null;
    }

    /// <summary>一つの診断本文が不正でも監視処理全体を停止しません。</summary>
    /// <returns>正しく復元できた値です。</returns>
    private static T? Deserialize<T>(JsonElement? value)
    {
        try
        {
            return value is null ? default
                                 : value.Value.Deserialize<T>(ContractJson.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
