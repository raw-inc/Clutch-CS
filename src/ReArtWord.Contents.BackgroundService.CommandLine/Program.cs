using System.Diagnostics.CodeAnalysis;

namespace ReArtWord.Contents.BackgroundService.CommandLine;

/// <summary>CLIの標準入出力と取消を管理します。</summary>
internal static class Program
{
    /// <summary>管理操作の実施結果をプロセス終了コードへ返します。</summary>
    /// <returns>CLIの終了コードです。</returns>
    // 抑制理由: C#の固定エントリポイントにはCancellationTokenとAsync接尾辞を追加できません。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "C#実行エントリポイントの固定signatureです。"),
     SuppressMessage("SourceQuality", "RAW601", Justification = "C#実行エントリポイントはMainです。")]
    private static async Task<int> Main(string[] args)
    {
        string secret = "secret";
        string stdin = "--stdin";
        if (args.Contains(secret, StringComparer.Ordinal) && args.Contains(stdin, StringComparer.Ordinal) && !Console.IsInputRedirected)
        {
            await Console.Error.WriteLineAsync("secret stdin must be redirected; interactive echo is not permitted").ConfigureAwait(false);
            return 2;
        }
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler stop = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += stop;
        try
        {
            return await CommandLineApplication.RunAsync(args, Console.In, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= stop;
        }
    }
}
