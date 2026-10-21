using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

namespace ReArtWord.Contents.BackgroundService.CoreService.Files;

/// <summary>通信回復後に終了済みのファイル予約を回収し、未確認の実処理は保持します。</summary>
internal sealed class FileReservationWorker(ServiceRuntime runtime) : Microsoft.Extensions.Hosting.BackgroundService
{
    /// <summary>履歴全体を走査せず、同時に一巡だけを有限時間で処理します。</summary>
    /// <returns>サービス停止までの予約保守です。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!runtime.Cluster.GetPeers().Any(peer => peer.InstanceId == runtime.Cluster.LocalInstanceId
                && (PeerStatusRules.CanParticipate(peer.Status) || peer.Status == EPeerStatus.Detached)))
            {
                continue;
            }
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                if (!runtime.Cluster.IsDetached)
                {
                    await runtime.FileTransactions.Transactions.RecoverReservationsAsync(false, deadline.Token).ConfigureAwait(false);
                }
                await runtime.FileTransactions.Transactions.RecoverReservationsAsync(true, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException
                or System.Data.Common.DbException or System.Text.Json.JsonException)
            {
                // 確認できない予約は維持し、次の周期で元の結果だけを再確認します。
            }
        }
    }
}
