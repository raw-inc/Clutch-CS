using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;

/// <summary>更新所有者の最小進捗をCore経由で観測し、通常ClientHostでも同じ更新を表示します。</summary>
internal sealed class FleetUpdateVisibility(FleetUpdateStore store, ObserverConnection connection) : IDisposable
{
    private readonly Guid publisher = Guid.NewGuid();
    private readonly Dictionary<Guid, (DateTimeOffset Version, DateTimeOffset Next)> published = [];
    private readonly FleetUpdateObservationStore observed = new();
    private readonly SemaphoreSlim reading = new(1, 1);
    private FleetUpdateOverview? cached;
    private DateTimeOffset nextRead;
    private long sequence;

    /// <summary>自身が所有する進捗だけを通知し、別ClientHostの観測を再発行しません。</summary>
    /// <returns>更新本体を変更しない有限の通知です。</returns>
    internal async Task PublishAsync(IReadOnlyList<FleetUpdateView> views, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (FleetUpdateView view in views.Where(view => !view.Completed || now - view.UpdatedAt < TimeSpan.FromMinutes(1)))
        {
            if (published.TryGetValue(view.OperationId, out (DateTimeOffset Version, DateTimeOffset Next) sent) && sent.Version == view.UpdatedAt && now < sent.Next)
            {
                continue;
            }
            published[view.OperationId] = (view.UpdatedAt, now.AddSeconds(5));
            FleetUpdateObservation notification = new(publisher, Interlocked.Increment(ref sequence), FleetUpdateActivity.From(view));
            string[] clusters = view.Peers.Select(peer => peer.Target.ClusterId).Distinct(StringComparer.Ordinal).ToArray();
            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await Parallel.ForEachAsync(Targets(clusters), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token }, async (target, token) =>
                {
                    try
                    {
                        ManagementCommand command = Command(target, "updates.fleet.observe", ContractJson.ToValue(notification));
                        _ = await target.Observer.NotifyAsync(command, token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or OperationCanceledException)
                    {
                        // 表示通知の欠測で更新操作を再実行せず、次の有限な状態通知で回復します。
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 対象の再起動中は通知先を次回の実発見情報で選び直します。
            }
        }
        foreach (Guid id in published.Keys.Where(id => !views.Any(view => view.OperationId == id)).ToArray())
        {
            published.Remove(id);
        }
    }

    /// <summary>全画面の照会をまとめ、到達不能な通知元の進捗を完了扱いで消しません。</summary>
    /// <returns>所有する操作と読取り専用の外部観測です。</returns>
    internal async Task<FleetUpdateOverview> ReadAsync(CancellationToken cancellationToken = default)
    {
        await reading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cached is not null && DateTimeOffset.UtcNow < nextRead)
            {
                return cached;
            }
            IReadOnlyList<FleetUpdateView> owned = await store.ListViewsAsync(cancellationToken).ConfigureAwait(false);
            Target[] targets = Targets(null).ToArray();
            System.Collections.Concurrent.ConcurrentDictionary<string, bool> reached = new(StringComparer.Ordinal);
            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token }, async (target, token) =>
                {
                    try
                    {
                        ManagementReply reply = await target.Observer.InspectAsync(Command(target, "updates.fleet.observed", null), token).ConfigureAwait(false);
                        string missing = "Management.OperationMissing", mismatch = "Management.OperationModeMismatch";
                        if (string.Equals(reply.Reason, missing, StringComparison.Ordinal) || string.Equals(reply.Reason, mismatch, StringComparison.Ordinal))
                        {
                            reached[target.Cluster] = true;
                            return;
                        }
                        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
                        {
                            return;
                        }
                        foreach (FleetUpdateObservation value in reply.Value.Json.Deserialize<FleetUpdateObservation[]>(ContractJson.Options) ?? [])
                        {
                            observed.Observe(value);
                        }
                        reached[target.Cluster] = true;
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or OperationCanceledException or ArgumentException)
                    {
                        // 他の到達済みCoreの結果を維持し、古い観測には鮮度期限を適用します。
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 更新中の到達不能を画面へ返し、元の更新要求には触れません。
            }
            cancellationToken.ThrowIfCancellationRequested();
            cached = new(owned, observed.Read().Where(item => !owned.Any(view => view.OperationId == item.Activity.OperationId)).ToArray(),
                targets.Select(target => target.Cluster).Distinct(StringComparer.Ordinal).Any(cluster => !reached.ContainsKey(cluster)));
            nextRead = DateTimeOffset.UtcNow.AddSeconds(2);
            return cached;
        }
        finally
        {
            reading.Release();
        }
    }

    /// <summary>各クラスタの二つの実体へ分散し、一つの更新停止で表示を失いにくくします。</summary>
    /// <returns>現在観測したReadyの通信先です。</returns>
    private IEnumerable<Target> Targets(IReadOnlyCollection<string>? selected)
    {
        foreach (Contracts.Management.Discovery.DiscoveredCluster cluster in connection.GetClusters().Where(cluster => selected is null || selected.Contains(cluster.ClusterId, StringComparer.Ordinal)))
        {
            foreach (PeerInfo peer in cluster.Peers.Where(peer => peer.Status == EPeerStatus.Ready && DateTimeOffset.UtcNow - peer.LastSeen < TimeSpan.FromSeconds(15)).OrderBy(peer => peer.PeerId, StringComparer.Ordinal).Take(2))
            {
                if (connection.FindObserver(cluster.ClusterId, peer.PeerId) is { } observer)
                {
                    yield return new(cluster.ClusterId, peer, observer);
                }
            }
        }
    }

    /// <summary>実体と期限を固定した管理要求です。</summary>
    /// <returns>永続的な更新命令とは別の照会・一時通知です。</returns>
    private static ManagementCommand Command(Target target, string operation, Contracts.Nodes.NodeValue? value)
        => new(Guid.NewGuid(), target.Cluster, target.Peer.PeerId, target.Peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(3), operation, value);

    /// <summary>進捗の読取りが終了した後に排他を解放します。</summary>
    public void Dispose() => reading.Dispose();

    /// <summary>同じ観測の対象と通信経路を固定します。</summary>
    private sealed record Target(string Cluster, PeerInfo Peer, UdpObserver Observer);
}
