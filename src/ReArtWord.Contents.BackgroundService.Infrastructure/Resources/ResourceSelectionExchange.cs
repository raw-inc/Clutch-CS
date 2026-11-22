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
    private static string AgeKind => "resources.selection.elapsed";
    private static readonly HashSet<string> metricNames = new(StringComparer.Ordinal)
    {
        "cpu.usage", "memory.usage", "memory.used", "gpu.usage", "gpu.memory.usage", "gpu.memory.used", "gpu.memory.total",
    };
    private readonly PeerCluster cluster;
    private readonly Func<ResourceSample?> latest;
    private readonly ConcurrentDictionary<string, ResourceSample> samples = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ResourceSelectionObservation> observations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, EObservationProtocol> capabilities = [];
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
        cluster.RegisterInspection(AgeKind, (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            ResourceSample? sample = latest();
            ResourceSelectionAgeSnapshot result = new(sample is null ? null
                                                                     : SelectMetrics(sample), sample?.Age?.Elapsed);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(result)));
        });
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

    /// <summary>他Peerへの照会が設定の反映を妨げないよう、計測値の交換を独立した3秒周期の処理で行います。</summary>
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

    /// <summary>他Peerの最新の計測値を照会し、自分の計測値は旧版向けにも通知します。</summary>
    /// <returns>計測値の送受信が一巡するまでの処理です。</returns>
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

    /// <summary>採取から応答までの時間と、要求開始からの経過時間を使って、計測値の古さを評価します。</summary>
    /// <returns>指定したPeerの計測値を照会し、検証して保存する処理です。</returns>
    private async ValueTask RefreshPeerAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        if (capabilities.TryGetValue(peer.InstanceId, out EObservationProtocol supported) && supported == EObservationProtocol.Legacy)
        {
            return;
        }
        long requestedAt = clock.GetTimestamp();
        try
        {
            (ClusterResponse response, EObservationProtocol protocol) = await ReadPeerAsync(peer, cancellationToken).ConfigureAwait(false);
            if (!cluster.GetPeers().Any(current => string.Equals(current.PeerId, peer.PeerId, StringComparison.Ordinal) && current.InstanceId == peer.InstanceId))
            {
                return;
            }
            if (response.Outcome != EOutcome.Succeeded)
            {
                return;
            }
            samples.TryRemove(peer.PeerId, out _);
            ResourceSample? sample;
            TimeSpan? sourceAge;
            if (protocol == EObservationProtocol.Elapsed)
            {
                ResourceSelectionAgeSnapshot? snapshot = response.Value?.Json.Deserialize<ResourceSelectionAgeSnapshot>(ContractJson.Options);
                sample = snapshot?.Sample;
                sourceAge = snapshot?.Age;
            }
            else
            {
                ResourceSelectionSnapshot? snapshot = response.Value?.Json.Deserialize<ResourceSelectionSnapshot>(ContractJson.Options);
                sample = snapshot?.Sample;
                sourceAge = snapshot?.ObservedAt - sample?.ObservedAt;
            }
            if (sample is null || sample.InstanceId != peer.InstanceId || !string.Equals(sample.PeerId, peer.PeerId, StringComparison.Ordinal)
                || sample.Metrics is null || 128 < sample.Metrics.Count)
            {
                return;
            }
            if (sourceAge is not { } age || age < TimeSpan.Zero || PcResourceComparison.MaximumAge < age)
            {
                return;
            }

            // 同じ標本でも、採取元の時刻補正によって採取からの経過時間が小さく計算されることがあります。
            // 再照会で有効期間が延びないよう、同じ標本IDでは最初の観測と計測開始点を残します。
            ResourceSelectionObservation observation = new(SelectMetrics(sample), new(clock, age + clock.GetElapsedTime(requestedAt)));
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
            // 照会に失敗しても、時計差を補正できない旧形式の通知には切り替えません。
            // 最後に確認できた標本を保持し、通信時間も含めた経過時間が10秒を超えたら比較対象から外します。
        }
    }

    /// <summary>未対応の応答を確認した場合だけ従来の照会方式を使い、通信失敗では降格しません。</summary>
    /// <returns>応答と、その本文に使われている観測形式です。</returns>
    private async Task<(ClusterResponse Response, EObservationProtocol Protocol)> ReadPeerAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        EObservationProtocol protocol = capabilities.GetValueOrDefault(peer.InstanceId, EObservationProtocol.Elapsed);
        ClusterResponse response = await ReadAsync(protocol).ConfigureAwait(false);
        string unsupported = "Cluster.InspectionTargetUnavailable";
        if (protocol == EObservationProtocol.Elapsed && response.Outcome == EOutcome.NotExecuted && string.Equals(response.Reason, unsupported, StringComparison.Ordinal))
        {
            protocol = EObservationProtocol.Timestamp;
            response = await ReadAsync(protocol).ConfigureAwait(false);
        }
        if (response.Outcome == EOutcome.NotExecuted && string.Equals(response.Reason, unsupported, StringComparison.Ordinal))
        {
            capabilities[peer.InstanceId] = EObservationProtocol.Legacy;
        }
        else if (response.Outcome == EOutcome.Succeeded)
        {
            capabilities[peer.InstanceId] = protocol;
        }
        return (response, protocol);

        Task<ClusterResponse> ReadAsync(EObservationProtocol version)
        {
            string kind = version == EObservationProtocol.Elapsed ? AgeKind
                                                                  : CurrentKind;
            ClusterRequest request = new(Guid.NewGuid(), kind, peer.PeerId, peer.InstanceId, clock.GetUtcNow().AddSeconds(3), NodeValue.Null);
            return cluster.InspectAsync(request, cancellationToken);
        }
    }

    /// <summary>新しい照会方式に未対応と確認できたPeerからだけ、旧形式の計測値を受け取ります。</summary>
    /// <returns>検証と最新値更新の完了。</returns>
    private Task ReceiveAsync(string peerId, Guid instance, NodeValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ResourceSample? sample = value.Json.Deserialize<ResourceSample>(ContractJson.Options);
            if (sample is null || sample.InstanceId != instance || !string.Equals(sample.PeerId, peerId, StringComparison.Ordinal)
                || !capabilities.TryGetValue(instance, out EObservationProtocol supported) || supported != EObservationProtocol.Legacy
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

    /// <summary>起動実体ごとに確認した観測形式です。</summary>
    private enum EObservationProtocol
    {
        /// <summary>日時だけを含む従来の一時通知です。</summary>
        Legacy,

        /// <summary>採取元の応答日時を添える旧照会です。</summary>
        Timestamp,

        /// <summary>採取元が計測した経過時間を返す照会です。</summary>
        Elapsed,
    }
}
