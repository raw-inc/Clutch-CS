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

/// <summary>修復画面の復元方式と、変更内容の表示を検証します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class ArchiveRepairTests
{
    /// <summary>日本語・英語の画面で入力した内容が、復元の確認画面にも反映されることを確認します。</summary>
    /// <returns>画面操作と表示内容の検証。</returns>
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

    /// <summary>復元先が未選択の場合、ファイルを開く前にエラーになることを確認します。</summary>
    /// <returns>復元先がない場合のエラー処理の検証。</returns>
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
