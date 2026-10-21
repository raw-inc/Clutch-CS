using System.Data.Common;
using System.Net.Sockets;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;

/// <summary>ローカル照会の失敗をHost終了と取り違えず、参加不能も継続通知します。</summary>
internal static class PeerHeartbeat
{
    /// <summary>状態取得と通知の障害境界を分け、実際の停止要求まで周期処理を維持します。</summary>
    /// <returns>停止までの生存通知です。</returns>
    // 抑制理由: refreshingは一回だけ所有し、次回開始前とfinallyで必ずawaitします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "状態確認Taskは次回開始前と終了時に必ずawaitします。")]
    internal static async Task RunAsync(TimeSpan interval, TimeProvider clock, Func<CancellationToken, Task> refresh,
        Func<CancellationToken, Task> publish, Action unavailable, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        using PeriodicTimer timer = new(interval, clock);
        Task refreshing = Task.CompletedTask;
        try
        {
            do
            {
                if (refreshing.IsCompleted)
                {
                    await refreshing.ConfigureAwait(false);
                    refreshing = RefreshAsync();
                }
                try
                {
                    await publish(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or SocketException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    unavailable();
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 停止時も、所有する一回の状態確認が終了するまで待ちます。
        }
        finally
        {
            try
            {
                await refreshing.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 状態確認も同じ停止要求に従います。
            }
            finally
            {
                unavailable();
            }
        }

        async Task RefreshAsync()
        {
            // SQLiteや同期の排他待ちが同期的に続いても、生存通知の周期を止めません。
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            try
            {
                await refresh(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException or DbException or JsonException or InvalidDataException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                unavailable();
            }
        }
    }
}
