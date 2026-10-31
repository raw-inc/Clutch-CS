using System.Diagnostics;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests;

/// <summary>試験が起動したプロセスを、他の回収処理の失敗にかかわらず終了まで待ちます。</summary>
internal static class TestProcessCleanup
{
    /// <summary>各プロセスの停止を開始し、すべての回収が終わってから失敗を伝えます。</summary>
    /// <returns>プロセスと所有ハンドルの回収です。</returns>
    internal static Task StopAsync(params Process?[] processes) => Task.WhenAll(processes.Select(StopOneAsync));

    /// <summary>所有する実体の終了を確認してから、ハンドルを解放します。</summary>
    /// <returns>一つのプロセスの回収です。</returns>
    private static async Task StopOneAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception && process.HasExited)
                {
                    // 停止要求と自然終了が重なった場合も、終了確認まで進めます。
                }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }
}
