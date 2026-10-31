using System.Diagnostics;

namespace ReArtWord.Contents.BackgroundService.Runtime.Processes;

/// <summary>後処理を始める前に、対象プロセスのOS上の終了を確認します。</summary>
public static class ProcessCompletion
{
    /// <summary>待機せず、OSの終了シグナルを確認します。</summary>
    /// <returns>終了を確認できた場合はtrueです。</returns>
    public static bool HasExited(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        // WindowsのWaitForExit(0)は終了コードではなく、プロセスハンドルの終了シグナルを確認します。
        // HasExitedへ置き換えると、終了処理中のプロセスを完了済みと判断する経路が戻ります。
        // https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Windows.cs#L163-L187
        return process.WaitForExit(0);
    }

    /// <summary>既存の出力イベントの排出と、OSによるプロセス終了を待ちます。</summary>
    /// <returns>完全終了、または呼出元による取消です。</returns>
    public static async Task WaitAsync(Process process, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);

        // BeginOutputReadLine等で開始した出力イベントの排出も待つため、BCLの非同期待機を先に行います。
        // 呼出元が直接読み取っている標準出力や子孫プロセスの終了は、このメソッドの確認対象には含めません。
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        // .NETのWindows実装は互換性維持のため、OSの終了シグナルより先に終了コードを確認します。
        // WaitForExitAsyncにもHasExitedだけでイベント待機を省略する経路があり、ファイル解放前に戻り得ます。
        // https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessManager.Windows.cs#L285-L319
        // https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.cs#L1524-L1560
        // .NET 10.0.13でも、非同期待機後に終了シグナルが未通知で、所有ファイルを再取得できないことを再現しました。
        // ProcessCompletionTestsでは通常終了・強制終了の両方で、終了シグナルと所有ファイルの再取得を確認します。
        // 1msは再確認までの間隔であり、資源解放を推測する猶予時間ではありません。期限・取消は呼出元に従います。
        // 他プロセスが保持するファイルまで解放される保証や、待機取消による対象プロセスの停止は追加しません。
        // BCL側でOSの終了シグナルまで待つ実装になり、上記の回帰試験が追加確認なしで通る場合に撤去を検討します。
        while (!HasExited(process))
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }
}
