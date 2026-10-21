using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;

namespace ReArtWord.Contents.BackgroundService.DesktopFixture;

/// <summary>製品workerを実行し、注釈の共有領域だけを試験ごとに分離します。</summary>
internal static class Program
{
    /// <summary>pipeと共有名を渡します。製品の起動引数や既定の共有範囲は変更しません。</summary>
    /// <returns>通常切断は0、未対応OSまたは不正引数は2です。</returns>
    // 抑制理由: C#固定entrypointにはCancellationTokenとAsync接尾辞を追加できません。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "C#固定entrypointです。"),
     SuppressMessage("SourceQuality", "RAW601", Justification = "C#固定entrypointです。")]
    private static async Task<int> Main(string[] args)
    {
        string clipboard = "clipboard-isolated";
        string wake = "wake-relay";
        string power = "power-schedule";
        if (args.Length == 2 && string.Equals(args[0], power, StringComparison.Ordinal))
        {
            await PowerScheduleScenario.RunAsync(args[1]).ConfigureAwait(false);
            return 0;
        }
        if (args.Length == 2 && string.Equals(args[0], wake, StringComparison.Ordinal))
        {
            await WakeScenario.RunAsync(args[1]).ConfigureAwait(false);
            return 0;
        }
        if (OperatingSystem.IsWindows() && args.Length == 1 && string.Equals(args[0], clipboard, StringComparison.Ordinal))
        {
            return ClipboardScenario.Run();
        }
        if (!OperatingSystem.IsWindows() || args.Length != 2)
        {
            return 2;
        }
        await DesktopWorker.RunAsync(args[0], "unused-encoder-no-video-selected", args[1]).ConfigureAwait(false);
        return 0;
    }
}
