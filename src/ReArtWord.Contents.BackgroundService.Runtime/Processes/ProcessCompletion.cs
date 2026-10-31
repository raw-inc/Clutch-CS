using System.Diagnostics;

namespace ReArtWord.Contents.BackgroundService.Runtime.Processes;

/// <summary>終了後にファイルやOS資源を回収できるまで、プロセスの完全終了を確認します。</summary>
public static class ProcessCompletion
{
    /// <summary>待機せず、OSの終了シグナルを確認します。</summary>
    /// <returns>終了を確認できた場合はtrueです。</returns>
    public static bool HasExited(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return process.WaitForExit(0);
    }

    /// <summary>既存の出力イベントの排出と、OSによるプロセス終了を待ちます。</summary>
    /// <returns>完全終了、または呼出元による取消です。</returns>
    public static async Task WaitAsync(Process process, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        // WindowsのHasExitedは終了コードを先に返すため、非同期の終了待ち後にもファイルが開いている場合があります。
        // https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessManager.Windows.cs#L260-L275
        // 固定時間ではなく終了シグナルを待ち、呼出元の期限・取消を維持します。
        while (!HasExited(process))
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }
}
