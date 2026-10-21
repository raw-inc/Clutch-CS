using System.IO;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.CommandLine;

/// <summary>標準入出力を汚さない headless worker の入口。</summary>
internal static class Program
{
    private static string versionOption => "--version";
    private static string helpOption => "--help";
    private static string pipeOption => "--pipe";
    private static string demoOption => "--demo";
    private static string demoRateOption => "--demo-rate";

    /// <summary>バイナリ pipe を開始します。</summary>
    /// <returns>プロセス終了コード。</returns>
    private static int Main(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    /// <summary>起動引数を検証し、非同期 pipe を実行します。</summary>
    /// <returns>プロセス終了コード。</returns>
    // 抑制理由: プロセス最上位で秘密を出さず終了コードへ変換し、キャンセル不能な標準pipeはプロセス寿命で所有します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "最上位の失敗を固定メッセージと終了コードへ変換します。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "標準pipeの保留read/writeをDisposeで待ち続けないよう、プロセス終了で回収します。")]
    private static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 1 && string.Equals(args[0], versionOption, StringComparison.Ordinal))
        {
            await Console.Out.WriteLineAsync((typeof(Program).Assembly.GetName().Version?.ToString() ?? string.Empty).AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (args.Length == 1 && string.Equals(args[0], helpOption, StringComparison.Ordinal))
        {
            await Console.Out.WriteLineAsync("SignalCapture --pipe [--demo [--demo-rate 1..100000]]\nBinary framed stdin/stdout, protocolVersion 1. Capture only.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        int demoRate = 10;
        bool demo = args.Length is 2 or 4 && string.Equals(args[0], pipeOption, StringComparison.Ordinal) && string.Equals(args[1], demoOption, StringComparison.Ordinal);
        bool rateValid = args.Length != 4 || string.Equals(args[2], demoRateOption, StringComparison.Ordinal) && int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out demoRate) && demoRate is >= 1 and <= 100000;
        if (!rateValid || !demo && (args.Length != 1 || !string.Equals(args[0], pipeOption, StringComparison.Ordinal)))
        {
            await Console.Error.WriteLineAsync("Use SignalCapture --pipe [--demo [--demo-rate 1..100000]].".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 2;
        }

        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            return await new PipeHost(demo, demoRate).RunAsync(Console.OpenStandardInput(), StandardOutputDangerous.Open(), cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return 0;
        }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync("SignalCapture failed. No packet content is included in this diagnostic.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }
    }
}
