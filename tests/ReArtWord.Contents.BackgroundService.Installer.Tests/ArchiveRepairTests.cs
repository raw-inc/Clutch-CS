using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>修復画面で復元方式を選択し、削除を含む確認内容を表示します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class ArchiveRepairTests
{
    /// <summary>日英の実画面でアーカイブ選択・復元方式・確認一覧が一致します。</summary>
    /// <returns>入力と表示の検証です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ArchiveRestoreShowsTheModeAndAllDestructiveChanges(int language)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(() =>
        {
            InstallationViewModel settings = new();
            WizardViewModel model = new(settings, new(settings), new TestInteraction())
            {
                OperationIndex = (int)EInstallerOperation.Repair, LanguageIndex = language, Page = EWizardPage.Maintenance,
            };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.Repair.Automatic);
            StackPanel fields = window.FindControl<StackPanel>("ArchiveRestoreFields")!;
            Assert.False(fields.IsEffectivelyVisible);
            RadioButton archive = window.FindControl<RadioButton>("ArchiveRestoreOption")!;
            Assert.True(archive.Focus());
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.Repair.RestoreArchive);
            Assert.False(settings.Repair.Reinstall);
            Assert.False(settings.Repair.Automatic);
            Assert.True(fields.IsEffectivelyVisible);
            window.FindControl<TextBox>("ArchiveFilePath")!.Text = "C:/Backups/studio.zip";
            window.FindControl<ComboBox>("ArchiveImportMode")!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EArchiveImportMode.Replace, settings.Repair.Archive.Mode);
            Assert.Equal("C:/Backups/studio.zip", settings.Repair.Archive.Path);
            settings.Repair.Archive.Preview = new(Guid.NewGuid(), "confirmation", EArchiveImportMode.Replace, "studio", "screen-a", DateTimeOffset.UtcNow,
                ["added-flow"], ["replaced-flow"], ["deleted-flow"], 5);
            string summary = WizardSummary.Create(settings);
            Assert.Contains("added-flow", summary, StringComparison.Ordinal);
            Assert.Contains("replaced-flow", summary, StringComparison.Ordinal);
            Assert.Contains("deleted-flow", summary, StringComparison.Ordinal);
            Assert.Contains(model.Text["ArchiveRestoreHelp"], summary, StringComparison.Ordinal);
            using Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            string directory = Path.GetFullPath("../../../../../.work/new-features/archive", AppContext.BaseDirectory);
            Directory.CreateDirectory(directory);
            frame.Save(Path.Combine(directory, "archive-repair-" + language + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>対象を指定せず、ほかのクラスターへ暗黙に切り替えて転送しません。</summary>
    /// <returns>操作開始前の拒否です。</returns>
    [Fact]
    public async Task MissingTargetFailsBeforeOpeningTheArchive()
    {
        InstallationViewModel settings = new();
        settings.Repair.Mode = EInstallerRepairMode.Archive;
        settings.Repair.Archive.Path = "missing.zip";
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallerArchive.PrepareAsync(settings));
        Assert.Null(settings.Repair.Archive.Preview);
        Assert.Null(settings.Repair.Archive.Target);
    }
}
