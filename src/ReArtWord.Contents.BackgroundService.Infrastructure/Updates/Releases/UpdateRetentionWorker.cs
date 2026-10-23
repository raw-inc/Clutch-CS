namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

/// <summary>起動直後の参加確認を待ち、Hostの寿命内で旧更新実行物を一度整理します。</summary>
public sealed class UpdateRetentionWorker : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly Func<CancellationToken, Task> trim;
    private readonly TimeSpan startupDelay;

    /// <summary>起動直後の処理から30秒離して、一度だけ整理します。</summary>
    public UpdateRetentionWorker(Func<CancellationToken, Task> trim) : this(trim, TimeSpan.FromSeconds(30))
    {
    }

    /// <summary>起動猶予を指定し、整理処理の障害境界を独立して検証できるようにします。</summary>
    internal UpdateRetentionWorker(Func<CancellationToken, Task> trim, TimeSpan startupDelay)
    {
        ArgumentNullException.ThrowIfNull(trim);
        ArgumentOutOfRangeException.ThrowIfLessThan(startupDelay, TimeSpan.Zero);
        this.trim = trim;
        this.startupDelay = startupDelay;
    }

    /// <summary>起動・通信を整理処理から分離し、停止時は実処理の終了を待ちます。</summary>
    /// <returns>この起動で一回の整理です。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(startupDelay, stoppingToken).ConfigureAwait(false);
            await trim(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 削除済み区間と未処理区間を操作記録から次回も区別できます。
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 整理は補助処理です。未知の検証エラーもHostを停止させず、次の起動で再確認します。
            try
            {
                await Console.Error.WriteLineAsync($"Update storage cleanup could not complete ({exception.GetType().Name}): {exception.Message}; retained files will be checked at the next start.").ConfigureAwait(false);
            }
            catch (Exception diagnostic) when (diagnostic is not OutOfMemoryException)
            {
                // 診断出力の失敗もHostの寿命へ波及させません。
            }
        }
    }
}
