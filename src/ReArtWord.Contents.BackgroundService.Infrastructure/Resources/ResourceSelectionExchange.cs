using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources;

/// <summary>実行PCの比較に必要な最新値だけを共有し、永続履歴や追加採取を増やしません。</summary>
public sealed class ResourceSelectionExchange
{
    private static string Topic => "resources.selection";
    private static string CurrentKind => "resources.selection.current";
    private static readonly HashSet<string> metricNames = new(StringComparer.Ordinal)
    {
        "cpu.usage", "memory.usage", "memory.used", "gpu.usage", "gpu.memory.usage", "gpu.memory.used", "gpu.memory.total",
    };
    private readonly PeerCluster cluster;
    private readonly Func<ResourceSample?> latest;
    private readonly ConcurrentDictionary<string, ResourceSample> samples = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ResourceSelectionObservation> observations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, bool> capabilities = [];
    private readonly TimeProvider clock;

    /// <summary>共通採取の参照元と、既存の一時通知経路を接続します。</summary>
    public ResourceSelectionExchange(PeerCluster cluster, Func<ResourceSample?> latest, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(latest);
        this.cluster = cluster;
        this.latest = latest;
        this.clock = clock ?? TimeProvider.System;
        cluster.RegisterTransientHandler(Topic, ReceiveAsync);
        cluster.RegisterInspection(CurrentKind, (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            ResourceSample? sample = latest();
            if (sample is not null)
            {
                sample = SelectMetrics(sample);
            }
            ResourceSelectionSnapshot result = new(sample, this.clock.GetUtcNow());
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(result)));
        });
    }

    /// <summary>一件の計画に使用するローカル・遠隔の観測を取得します。</summary>
    /// <returns>Peerごとの採取済み観測。鮮度は選択時に検証します。</returns>
    public IReadOnlyDictionary<string, ResourceSample> Capture()
    {
        Dictionary<string, ResourceSample> snapshot = samples.ToDictionary(StringComparer.Ordinal);
        foreach ((string peer, ResourceSelectionObservation observation) in observations)
        {
            snapshot[peer] = observation.Capture(clock);
        }
        if (latest() is { } local)
        {
            snapshot[cluster.LocalPeerId] = SelectMetrics(local);
        }
        return snapshot;
    }

    /// <summary>設定反映を通信待ちで止めず、最新観測の交換を3秒周期で独立して実行します。</summary>
    /// <returns>停止要求までの交換処理です。</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(3), clock);
        do
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>採取済みの比較値を一巡交換します。追加採取や永続化は行いません。</summary>
    /// <returns>有限の照会と、旧版向け通知の送信です。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        PeerInfo[] peers = cluster.GetPeers().ToArray();
        HashSet<string> known = peers.Select(peer => peer.PeerId).ToHashSet(StringComparer.Ordinal);
        foreach (string peer in samples.Keys.Where(peer => !known.Contains(peer)))
        {
            samples.TryRemove(peer, out _);
        }
        foreach ((string peer, ResourceSelectionObservation observation) in observations)
        {
            if (!peers.Any(current => string.Equals(current.PeerId, peer, StringComparison.Ordinal) && current.InstanceId == observation.Sample.InstanceId))
            {
                observations.TryRemove(peer, out _);
            }
        }
        foreach (Guid instance in capabilities.Keys.Where(instance => !peers.Any(peer => peer.InstanceId == instance)))
        {
            capabilities.TryRemove(instance, out _);
        }
        try
        {
            if (latest() is { } sample)
            {
                await cluster.PublishTransientAsync(Topic, NodeValue.FromObject(SelectMetrics(sample)), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            // 通知不能でも採取と通常処理は続けます。相手側は観測の期限切れを選択不能として示します。
        }
        await Parallel.ForEachAsync(peers.Where(peer => peer.InstanceId != cluster.LocalInstanceId),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, RefreshPeerAsync).ConfigureAwait(false);
    }

    /// <summary>応答の採取元時刻を使い、往復の通信時間を差し引かず鮮度に含めます。</summary>
    /// <returns>そのPeer実体の比較値の更新です。</returns>
    private async ValueTask RefreshPeerAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        if (capabilities.TryGetValue(peer.InstanceId, out bool supported) && !supported)
        {
            return;
        }
        long requestedAt = clock.GetTimestamp();
        ClusterRequest request = new(Guid.NewGuid(), CurrentKind, peer.PeerId, peer.InstanceId, clock.GetUtcNow().AddSeconds(3), NodeValue.Null);
        try
        {
            ClusterResponse response = await cluster.InspectAsync(request, cancellationToken).ConfigureAwait(false);
            if (!cluster.GetPeers().Any(current => string.Equals(current.PeerId, peer.PeerId, StringComparison.Ordinal) && current.InstanceId == peer.InstanceId))
            {
                return;
            }
            string unavailable = "Cluster.InspectionTargetUnavailable";
            if (response.Outcome == EOutcome.NotExecuted && string.Equals(response.Reason, unavailable, StringComparison.Ordinal))
            {
                capabilities[peer.InstanceId] = false;
                return;
            }
            if (response.Outcome != EOutcome.Succeeded)
            {
                return;
            }
            capabilities[peer.InstanceId] = true;
            samples.TryRemove(peer.PeerId, out _);
            ResourceSelectionSnapshot? snapshot = response.Value?.Json.Deserialize<ResourceSelectionSnapshot>(ContractJson.Options);
            if (snapshot?.Sample is not { } sample || sample.InstanceId != peer.InstanceId || !string.Equals(sample.PeerId, peer.PeerId, StringComparison.Ordinal)
                || sample.Metrics is null || 128 < sample.Metrics.Count)
            {
                return;
            }
            TimeSpan age = snapshot.ObservedAt - sample.ObservedAt;
            if (age < TimeSpan.Zero || PcResourceComparison.MaximumAge < age)
            {
                return;
            }

            // 同じ標本の再照会では基準を取り直しません。応答待ちと収集停止の時間を保持します。
            ResourceSelectionObservation observation = new(SelectMetrics(sample), requestedAt, age);
            observations.AddOrUpdate(peer.PeerId, observation, (_, previous) =>
            {
                if (previous.Sample.InstanceId == sample.InstanceId && previous.Sample.Id == sample.Id)
                {
                    return previous;
                }
                return observation;
            });
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or JsonException or InvalidOperationException or ArgumentException)
        {
            // 一度対応を確認した実体では旧通知へ戻しません。最後の標本が自然に期限切れになります。
        }
    }

    /// <summary>通知元の識別と実体が観測と一致する場合だけ最新値を更新します。</summary>
    /// <returns>検証と最新値更新の完了。</returns>
    private Task ReceiveAsync(string peerId, Guid instance, NodeValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ResourceSample? sample = value.Json.Deserialize<ResourceSample>(ContractJson.Options);
            if (sample is null || sample.InstanceId != instance || !string.Equals(sample.PeerId, peerId, StringComparison.Ordinal)
                || !capabilities.TryGetValue(instance, out bool supported) || supported
                || sample.Metrics is null || 128 < sample.Metrics.Count || !cluster.GetPeers().Any(peer => string.Equals(peer.PeerId, peerId, StringComparison.Ordinal) && peer.InstanceId == instance))
            {
                return Task.CompletedTask;
            }
            samples.AddOrUpdate(peerId, sample, (_, previous) =>
            {
                if (previous.InstanceId != instance || previous.ObservedAt <= sample.ObservedAt)
                {
                    return sample;
                }
                return previous;
            });
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            // 他の通知と共通採取を継続し、不正な観測を正常値として取り込みません。
        }
        return Task.CompletedTask;
    }

    /// <summary>比較に使わないCPU情報、NIC統計、履歴を転送しません。</summary>
    /// <returns>有限な比較用観測。保持上限超過時は欠測として送ります。</returns>
    private static ResourceSample SelectMetrics(ResourceSample sample)
    {
        ResourceMetric[] metrics = sample.Metrics.Where(metric => metricNames.Contains(metric.Name)).Take(129).ToArray();
        if (128 < metrics.Length)
        {
            metrics = [];
        }
        return sample with { Metrics = metrics, HistoryStored = false, HistoryPending = false };
    }
}
