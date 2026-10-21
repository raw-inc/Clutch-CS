using ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting.Workers;

/// <summary>通常の稼働・死活確認を保存待ちで止めず、明示した自動反映だけを監視します。</summary>
internal sealed class DraftAutoApplyWorker(DraftAutoApply apply) : Microsoft.Extensions.Hosting.BackgroundService
{
    /// <summary>Host停止とともに監視を終了します。</summary>
    /// <returns>所有する自動反映処理の寿命です。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await apply.TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested && error is IOException or InvalidOperationException or OperationCanceledException)
            {
                // 元要求は保存側で保持され、次の一巡で副作用を再送しません。
            }
        }
    }
}
