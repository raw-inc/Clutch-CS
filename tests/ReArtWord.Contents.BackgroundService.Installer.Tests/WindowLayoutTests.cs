using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>表示プレビューと確認欄のスクロールを実画面で検証します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class WindowLayoutTests
{
    /// <summary>文字と画像の変更を反映し、空欄や入力途中では既定表示に戻します。</summary>
    /// <returns>プレビューの更新と画像解放の確認です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AppearancePreviewTracksInputsAndKeepsCompactFields(int language)
    {
        using SetupFixture fixture = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(async () =>
        {
            await fixture.InitializeAsync().ConfigureAwait(true);
            InstallationViewModel settings = new() { Advanced = true };
            WizardViewModel model = new(settings, new(settings), new TestInteraction()) { LanguageIndex = language, Page = EWizardPage.Appearance };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AppearancePreview preview = window.FindControl<AppearancePreview>("DisplayPreview")!;
            TextBox title = window.FindControl<TextBox>("ClientTitleField")!;
            TextBox subtitle = window.FindControl<TextBox>("ClientSubtitleField")!;
            TextBox icon = window.FindControl<TextBox>("ClientIconField")!;
            Assert.InRange(icon.Bounds.Height, title.Bounds.Height, title.Bounds.Height + icon.BorderThickness.Top + icon.BorderThickness.Bottom);
            Assert.Equal(ClientAppearance.DefaultTitle, preview.FindControl<TextBlock>("PreviewTitle")!.Text);
            Assert.Equal(ClientAppearance.DefaultSubtitle, preview.FindControl<TextBlock>("PreviewSubtitle")!.Text);
            Assert.Null(settings.Appearance.Capture().Title);
            Capture(window, $"installer-preview-default-{language}.png");
            title.Text = "Studio";
            subtitle.Text = "Control room";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Studio", preview.FindControl<TextBlock>("PreviewTitle")!.Text);
            Assert.Equal("Clutch-CS", preview.FindControl<TextBlock>("PreviewTabTitle")!.Text);
            Assert.Equal("Control room", preview.FindControl<TextBlock>("PreviewSubtitle")!.Text);
            string imagePath = Path.Combine(fixture.Root, "preview.ico");
            using (Stream source = AssetLoader.Open(new Uri("avares://ReArtWord.Contents.BackgroundService.Installer/Assets/Clutch-CS.ico")))
            using (FileStream destination = File.Create(imagePath))
            {
                await source.CopyToAsync(destination).ConfigureAwait(true);
            }
            icon.Text = imagePath;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(preview.FindControl<Image>("TabImage")!.Source);
            window.FindControl<TextBox>("ClientFaviconField")!.Text = imagePath;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(preview.FindControl<Image>("HeaderImage")!.Source);
            Assert.NotNull(preview.FindControl<Image>("TabImage")!.Source);
            Assert.False(preview.FindControl<Viewbox>("DefaultHeaderIcon")!.IsVisible);
            Capture(window, $"installer-preview-custom-{language}.png");
            icon.Text = Path.Combine(fixture.Root, "not-selected-yet");
            subtitle.Text = string.Empty;
            title.Text = string.Empty;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(preview.FindControl<Image>("HeaderImage")!.Source);
            Assert.True(preview.FindControl<Viewbox>("DefaultHeaderIcon")!.IsVisible);
            Assert.Equal(ClientAppearance.DefaultTitle, preview.FindControl<TextBlock>("PreviewTitle")!.Text);
            Assert.Equal(ClientAppearance.DefaultSubtitle, preview.FindControl<TextBlock>("PreviewSubtitle")!.Text);
            window.Close();
            Assert.Null(preview.FindControl<Image>("TabImage")!.Source);
            File.Delete(imagePath);
            return true;
        }, CancellationToken.None);
    }

    /// <summary>確認内容が長くても、最小サイズの画面で同意欄を常に表示します。</summary>
    /// <returns>内容欄だけのスクロール確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewScrollsInsideTheBoxAndKeepsConsentVisible(bool uninstall)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(() =>
        {
            InstallationViewModel settings = new();
            WizardViewModel model = new(settings, new(settings), new TestInteraction()) { LanguageIndex = 1, Page = EWizardPage.Review };
            if (uninstall)
            {
                model.OperationIndex = (int)EInstallerOperation.Uninstall;
            }
            model.ReviewText = string.Join(Environment.NewLine, Enumerable.Range(1, 100).Select(index => "確認項目 " + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            InstallerWindow window = new() { DataContext = model };
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextBox box = window.FindControl<TextBox>("ReviewContent")!;
            CheckBox consent = window.FindControl<CheckBox>("ReviewConsent")!;
            ScrollViewer scroll = box.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert.True(scroll.Viewport.Height < scroll.Extent.Height);
            Point before = consent.TranslatePoint(default, window)!.Value;
            Assert.True(0 <= before.Y && before.Y + consent.Bounds.Height < window.ClientSize.Height);
            scroll.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            Assert.True(0 < scroll.Offset.Y);
            Assert.Equal(before, consent.TranslatePoint(default, window)!.Value);
            consent.IsChecked = true;
            Assert.True(settings.Confirmed);
            Capture(window, $"installer-review-scroll-{uninstall}.png");
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    /// <summary>指定された場合だけ描画結果を保存します。</summary>
    private static void Capture(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("CLUTCH_CAPTURE_DIRECTORY");
        if (directory is null)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
