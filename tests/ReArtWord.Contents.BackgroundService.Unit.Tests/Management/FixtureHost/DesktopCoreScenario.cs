using ReArtWord.Contents.BackgroundService.CoreService.Desktop;

namespace ReArtWord.Contents.BackgroundService.DesktopFixture;

/// <summary>製品のCoreを起動し、子ワーカーの注釈領域だけを試験用に分離します。</summary>
internal static class DesktopCoreScenario
{
    /// <summary>通常の設定ファイル起動と、Coreが開始する子ワーカーを処理します。</summary>
    /// <returns>製品の終了コード、または不正引数の2です。</returns>
    internal static async Task<int> RunAsync(string[] args, string annotationGroup, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationGroup);
        cancellationToken.ThrowIfCancellationRequested();
        string worker = "--desktop-worker";
        if (OperatingSystem.IsWindows() && args is [string mode, string pipe, string encoder]
            && string.Equals(mode, worker, StringComparison.Ordinal))
        {
            await DesktopWorker.RunAsync(pipe, encoder, annotationGroup, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        string configuration = "--config";
        if (args.Length != 2 || !string.Equals(args[0], configuration, StringComparison.Ordinal))
        {
            return 2;
        }

        // Coreの構成と起動手順を製品と揃えるため、アセンブリのエントリポイントを呼び出します。
        System.Reflection.MethodInfo entry = typeof(DesktopWorker).Assembly.EntryPoint
            ?? throw new InvalidOperationException("The Core entry point is unavailable.");
        object? result = entry.Invoke(null, [args]);
        return result switch
        {
            int code => code,
            Task<int> operation => await operation.ConfigureAwait(false),
            _ => throw new InvalidOperationException("The Core entry point returned an unexpected result."),
        };
    }
}
