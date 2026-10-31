namespace ReArtWord.Contents.BackgroundService.Unit.Tests;

/// <summary>試験が途中で失敗しても、開始した非同期処理を停止して終了まで待ちます。</summary>
internal sealed class TestTaskLifetime(Task running, CancellationTokenSource cancellation) : IAsyncDisposable
{
    /// <summary>通信先や保存先を破棄する前に、所有する処理を終了させます。</summary>
    /// <returns>取消に伴う後始末を含めた処理の終了です。</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // 取消は後始末のために行います。通常経路の結果は試験本体で検証します。
            }
        }
    }
}
