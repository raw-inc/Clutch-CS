using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Concurrency;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>試験が中断しても、取得待ちを回収し、取得済みの枠を返します。</summary>
internal sealed class TestPermitLifetime(Task<WorkflowConcurrencyAdmission> pending, CancellationTokenSource cancellation) : IAsyncDisposable
{
    /// <summary>通信や調停側の資源を破棄する前に、元の取得と解放を完了させます。</summary>
    /// <returns>待機の終了と、返却された枠の解放です。</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            WorkflowConcurrencyAdmission? result = null;
            try
            {
                result = await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // 取得待ちの取消も終了確認です。通常経路の結果は試験本体で検証します。
            }
            if (result?.Lease is not null)
            {
                await result.Lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
