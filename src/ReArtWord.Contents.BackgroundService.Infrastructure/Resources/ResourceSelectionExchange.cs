using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources;

/// <summary>実行PCの比較に必要な最新値だけを共有し、永続履歴や追加採取を増やしません。</summary>
public sealed class ResourceSelectionExchange
{
    private static string Topic => "resources.selection";
    private static readonly HashSet<string> metricNames = new(StringComparer.Ordinal)
    {
        "cpu.usage", "memory.usage", "memory.used", "gpu.usage", "gpu.memory.usage", "gpu.memory.used", "gpu.memory.total",
    };
    private readonly PeerCluster cluster;
    private readonly Func<ResourceSample?> latest;
    private readonly ConcurrentDictionary<string, ResourceSample> samples = new(StringComparer.Ordinal);
    private DateTimeOffset nextPublish;

    /// <summary>共通採取の参照元と、既存の一時通知経路を接続します。</summary>
    public ResourceSelectionExchange(PeerCluster cluster, Func<ResourceSample?> latest)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(latest);
        this.cluster = cluster;
        this.latest = latest;
        cluster.RegisterTransientHandler(Topic, ReceiveAsync);
    }

    /// <summary>一件の計画に使用するローカル・遠隔の観測を取得します。</summary>
    /// <returns>Peerごとの採取済み観測。鮮度は選択時に検証します。</returns>
    public IReadOnlyDictionary<string, ResourceSample> Capture()
    {
        Dictionary<string, ResourceSample> snapshot = samples.ToDictionary(StringComparer.Ordinal);
        if (latest() is { } local)
        {
            snapshot[cluster.LocalPeerId] = SelectMetrics(local);
        }
        return snapshot;
    }

    /// <summary>最大3秒に一回、最新観測を送ります。欠落した通知は次回の値で回復します。</summary>
    /// <returns>今回必要だった通知の送信試行。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < nextPublish || latest() is not { } sample)
        {
            return;
        }
        nextPublish = now.AddSeconds(3);
        HashSet<string> known = cluster.GetPeers().Select(peer => peer.PeerId).ToHashSet(StringComparer.Ordinal);
        foreach (string peer in samples.Keys.Where(peer => !known.Contains(peer)))
        {
            samples.TryRemove(peer, out _);
        }
        try
        {
            await cluster.PublishTransientAsync(Topic, NodeValue.FromObject(SelectMetrics(sample)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            // 通知不能でも採取と通常処理は続けます。相手側は観測の期限切れを選択不能として示します。
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
                || 128 < sample.Metrics.Count || !cluster.GetPeers().Any(peer => string.Equals(peer.PeerId, peerId, StringComparison.Ordinal) && peer.InstanceId == instance))
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
