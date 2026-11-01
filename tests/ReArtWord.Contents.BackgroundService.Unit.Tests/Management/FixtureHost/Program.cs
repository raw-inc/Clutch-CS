using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;

namespace ReArtWord.Contents.BackgroundService.DesktopFixture;

/// <summary>管理機能の実プロセス試験を、指定した隔離条件で起動します。</summary>
internal static class Program
{
    /// <summary>試験用の起動モードを選び、対応する製品処理へ渡します。</summary>
    /// <returns>実行結果の終了コードです。</returns>
    // 抑制理由: C#固定entrypointにはCancellationTokenとAsync接尾辞を追加できません。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "C#固定entrypointです。"),
     SuppressMessage("SourceQuality", "RAW601", Justification = "C#固定entrypointです。")]
    private static async Task<int> Main(string[] args)
    {
        string core = "--config", desktop = "--desktop-worker";
        if (args.Length != 0 && (string.Equals(args[0], core, StringComparison.Ordinal) || string.Equals(args[0], desktop, StringComparison.Ordinal)))
        {
            string? group = Environment.GetEnvironmentVariable("CONTENTS_TEST_ANNOTATION_GROUP");
            if (string.IsNullOrWhiteSpace(group))
            {
                await Console.Error.WriteLineAsync("An isolated annotation group must be configured.").ConfigureAwait(false);
                return 2;
            }
            return await DesktopCoreScenario.RunAsync(args, group).ConfigureAwait(false);
        }
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
