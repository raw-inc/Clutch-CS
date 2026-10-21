namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

/// <summary>起動直後の参加確認を待ち、Hostの寿命内で旧更新実行物を一度整理します。</summary>
public sealed class UpdateRetentionWorker(Func<CancellationToken, Task> trim) : Microsoft.Extensions.Hosting.BackgroundService
{
    /// <summary>起動・通信を整理処理から分離し、停止時は実処理の終了を待ちます。</summary>
    /// <returns>この起動で一回の整理です。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            await trim(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 削除済み区間と未処理区間を操作記録から次回も区別できます。
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            await Console.Error.WriteLineAsync("Update storage cleanup could not complete; retained files will be checked at the next start.").ConfigureAwait(false);
        }
    }
}
