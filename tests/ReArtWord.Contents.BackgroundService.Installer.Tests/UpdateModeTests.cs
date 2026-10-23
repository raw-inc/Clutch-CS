using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>更新方式の選択と、対象のグループ分けを検証します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class UpdateModeTests
{
    /// <summary>画面で更新方式を一つ選択でき、確認画面にもその選択が反映されることを確認します。</summary>
    /// <returns>日本語・英語での画面操作の検証。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RadioChoicesPreserveTheSelectedMode(int language)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(() =>
        {
            InstallationViewModel settings = new() { Operation = EInstallerOperation.Update };
            WizardViewModel model = new(settings, new(settings), new TestInteraction())
            {
                LanguageIndex = language, OperationIndex = (int)EInstallerOperation.Update, Page = EWizardPage.Maintenance,
            };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            RadioButton skip = window.FindControl<RadioButton>("SkipActiveUpdateOption")!;
            RadioButton stop = window.FindControl<RadioButton>("StopActiveUpdateOption")!;
            RadioButton fleet = window.FindControl<RadioButton>("FleetUpdateOption")!;
            Assert.True(skip.IsChecked);
            foreach (RadioButton choice in new[] { fleet, stop, skip })
            {
                Assert.True(choice.IsEffectivelyVisible);
                Assert.True(choice.Focus());
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                Dispatcher.UIThread.RunJobs();
                Assert.True(choice.IsChecked);
                Assert.Single(new[] { skip, stop, fleet }, radio => radio.IsChecked == true);
                string summary = WizardSummary.Create(settings);
                Assert.Contains(choice.Content!.ToString()!, summary, StringComparison.Ordinal);
            }
            using Avalonia.Media.Imaging.WriteableBitmap? image = window.CaptureRenderedFrame();
            Assert.NotNull(image);
            string directory = Path.GetFullPath("../../../../../.work/new-features/fleet", AppContext.BaseDirectory);
            Directory.CreateDirectory(directory);
            image.Save(Path.Combine(directory, "update-modes-" + language + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>処理中の対象を除外する方式では、対象ごとに更新することを確認します。</summary>
    [Fact]
    public void SkipActiveSeparatesTargetsWhileStopActiveKeepsTheClusterTogether()
    {
        Contracts.Management.Discovery.ClientNetworkBinding binding = new("loopback", "127.0.0.1", "127.255.255.255", 45200);
        ObservedTarget[] targets = [new("studio", "screen-a", binding, null), new("studio", "screen-b", binding, null)];
        Assert.All(WizardUpdates.Groups(targets, EInstallerUpdateMode.SkipActive), group => Assert.Single(group));
        Assert.Equal(targets, Assert.Single(WizardUpdates.Groups(targets, EInstallerUpdateMode.StopActive)));
    }
}
