using Avalonia;
using Avalonia.Headless;
using ReArtWord.Contents.BackgroundService.Installer;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>実AXAMLをSkiaでheadless描画します。</summary>
internal static class InstallerTestApp
{
    /// <summary>OSのwindowを作らず同じ表示資材を読み込みます。</summary>
    /// <returns>実描画用のAppBuilderです。</returns>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<InstallerApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
