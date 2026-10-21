using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;

/// <summary>全体更新の永続結果をCoreの共通Hookへ渡し、更新本体とは独立して終了します。</summary>
internal sealed class FleetUpdateEvents : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly FleetUpdateStore store;
    private readonly ObserverConnection connection;
    private readonly string directory;
    private readonly FleetUpdateVisibility visibility;

    /// <summary>ClientHostの通知試行記録と、明示したObserver接続を使います。</summary>
    public FleetUpdateEvents(FleetUpdateStore store, ObserverConnection connection, ClientHostOptions options, FleetUpdateVisibility visibility)
    {
        this.store = store;
        this.connection = connection;
        this.visibility = visibility;
        directory = Path.Combine(options.StateDirectory, "update-event-deliveries");
    }

    /// <summary>更新結果そのものとは分離した通知上の問題です。</summary>
    public string? Problem
    {
        get; private set;
    }

    /// <summary>画面を閉じても、ClientHostが生きている間は保存結果を照会します。</summary>
    /// <returns>Host寿命までの通知監視です。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool initialized = false;
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                if (!initialized)
                {
                    Directory.CreateDirectory(directory);
                    await InitializeAsync(stoppingToken).ConfigureAwait(false);
                    initialized = true;
                }
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                Problem = "UpdateEvents.FleetObservationUnconfirmed";
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>初回導入時の完了済み履歴を、新しい更新の通知として再生しません。</summary>
    /// <returns>既存履歴を明示的に除外する初期化です。</returns>
    private async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string initialized = Path.Combine(directory, "initialized");
        if (File.Exists(initialized))
        {
            return;
        }
        foreach (FleetUpdateView status in (await store.ListViewsAsync(cancellationToken).ConfigureAwait(false)).Where(status => status.Completed))
        {
            foreach (string cluster in status.Peers.Select(target => target.Target.ClusterId).Distinct(StringComparer.Ordinal))
            {
                foreach (UpdateNotification notification in FleetUpdateNotifications.Capture(status, cluster))
                {
                    string marker = Path.Combine(directory, notification.Id.ToString("N") + ".json");
                    await File.WriteAllTextAsync(marker, "{\"historical\":true}", cancellationToken).ConfigureAwait(false);
                }
            }
        }
        await File.WriteAllTextAsync(initialized, "historical completed updates excluded", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>本体の再開・再試行を行わず、現在までに保存された各試行結果を読みます。</summary>
    /// <returns>接続可能なクラスタへの通知試行です。</returns>
    private async Task TickAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FleetUpdateView> views = await store.ListViewsAsync(cancellationToken).ConfigureAwait(false);
        await visibility.PublishAsync(views, cancellationToken).ConfigureAwait(false);
        foreach (FleetUpdateView status in views)
        {
            foreach (string cluster in status.Peers.Select(target => target.Target.ClusterId).Distinct(StringComparer.Ordinal))
            {
                PeerInfo? peer = connection.GetClusters().Where(item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal)).SelectMany(item => item.Peers)
                    .Where(item => item.Status == EPeerStatus.Ready && DateTimeOffset.UtcNow - item.LastSeen < TimeSpan.FromSeconds(15)).OrderBy(item => item.PeerId, StringComparer.Ordinal).FirstOrDefault();
                if (peer is null || connection.FindObserver(cluster, peer.PeerId) is not { } observer)
                {
                    continue;
                }
                foreach (UpdateNotification notification in FleetUpdateNotifications.Capture(status, cluster))
                {
                    await DeliverAsync(observer, cluster, peer, notification, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>一回の配送試行を先に記録し、応答不明で別Peerへ再発行しません。</summary>
    /// <returns>更新本体へ成否を伝播しない通知処理です。</returns>
    private async Task DeliverAsync(UdpObserver observer, string cluster, PeerInfo peer, UpdateNotification notification, CancellationToken cancellationToken = default)
    {
        string marker = Path.Combine(directory, notification.Id.ToString("N") + ".json");
        if (File.Exists(marker))
        {
            return;
        }
        using (FileStream file = new(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(file, new { notification.Id, notification.Data.OperationId, cluster, peer = peer.PeerId }, ContractJson.Options, cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            ManagementCommand command = new(notification.Id, cluster, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(4), "updates.events.enqueue", ContractJson.ToValue(notification));
            ManagementReply reply = await observer.ExecuteAsync(command, timeout.Token).ConfigureAwait(false);
            string outcome = "outcome", succeeded = "succeeded";
            if (reply.Outcome != EOutcome.Succeeded || reply.Value?.Json.TryGetProperty(outcome, out JsonElement result) != true || !string.Equals(result.GetString(), succeeded, StringComparison.Ordinal))
            {
                Problem = "UpdateEvents.FleetDeliveryUnconfirmedNoReplay";
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            Problem = "UpdateEvents.FleetDeliveryUnconfirmedNoReplay";
        }
    }
}
