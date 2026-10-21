using System.Diagnostics.CodeAnalysis;
using Avalonia;

namespace ReArtWord.Contents.BackgroundService.Installer;

/// <summary>管理者権限を要求しない対話画面の入口です。</summary>
internal static class Program
{
    /// <summary>必要な配置操作のときだけ別helperを昇格します。</summary>
    // 抑制理由: C#の同期entrypointの固定名・引数です。
    [STAThread, SuppressMessage("SourceQuality", "RAW601", Justification = "C# entry pointです。")]
    private static void Main(string[] args) => AppBuilder.Configure<InstallerApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}
