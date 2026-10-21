using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting.Workers;

/// <summary>継続実行中にも有限単位の保守を進め、更新中と設定競合中は保留します。</summary>
internal sealed class HistoryArchiveWorker(ServiceRuntime runtime, HistoryArchiveService archive) : Microsoft.Extensions.Hosting.BackgroundService
{
    /// <summary>開始済みワークフロー全体の終了を待たず、保存先の短い取引で保守します。</summary>
    /// <returns>停止まで所有する保守ループです。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HistoryRetentionPeerGate peers = new(runtime.Cluster);
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!runtime.ManagementStarted || !PeerStatusRules.CanParticipate(runtime.Status) || runtime.Maintenance.IsClosed)
            {
                continue;
            }
            ClusterOperationalSnapshot? effective = runtime.Operational.Capture();
            if (effective is null || effective.UnresolvedFields?.Contains(nameof(ClusterOperationalSettings.DataRetention), StringComparer.Ordinal) == true
                || effective.UnresolvedFields?.Contains(nameof(ClusterOperationalSettings.LogRetention), StringComparer.Ordinal) == true)
            {
                archive.Hold("History.RetentionConfigurationUnconfirmed");
                continue;
            }
            try
            {
                if (!await peers.ReadyAsync(stoppingToken).ConfigureAwait(false))
                {
                    archive.Hold("History.RetentionPeerUpgradeRequired");
                    continue;
                }
                await archive.MaintainAsync(runtime.Configuration.HistoryArchive, HistoryRetentionPolicy.Resolve(effective.Settings, runtime.Configuration.Logs),
                    DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
            {
                archive.Hold("History.RetentionUnconfirmed");
            }
        }
    }
}
