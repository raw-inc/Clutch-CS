using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>管理画面の起動権限を、Coreの権限と分けて選択します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class ClientStartupChoiceTests
{
    /// <summary>ユーザー起動を既定にし、高度な設定でだけサービス起動を選べます。</summary>
    /// <returns>日英の画面操作と実行設定の検証です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ServiceStartupIsExplicitAndIndependentOfCore(int language)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(async () =>
        {
            using SetupFixture fixture = new();
            await fixture.InitializeAsync().ConfigureAwait(true);
            InstallationViewModel settings = new() { Advanced = true };
            settings.Networks.Add(fixture.Settings.Network);
            WizardViewModel model = new(settings, new(settings), new TestInteraction()) { LanguageIndex = language, Page = EWizardPage.Advanced };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            CheckBox choice = window.FindControl<CheckBox>("WebClientServiceStartup")!;
            Assert.True(choice.IsEffectivelyVisible);
            Assert.False(choice.IsChecked);
            Assert.False(settings.Settings().WebClient!.RunAsService);
            Assert.True(choice.Focus());
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.True(settings.Settings().WebClient!.RunAsService);
            settings.PerUser = true;
            Assert.True(settings.Settings().WebClient!.RunAsService);
            settings.Advanced = false;
            Assert.False(settings.Settings().WebClient!.RunAsService);
            settings.Advanced = true;
            string? directory = Environment.GetEnvironmentVariable("CLUTCH_CAPTURE_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(directory, "installer-client-startup-" + language.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            window.Close();
            return true;
        }, CancellationToken.None);
    }
}
