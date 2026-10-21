using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>OSを変更せず、確認・実行・結果画面と詳細展開の実bindingを確認します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class ProgressTests
{
    /// <summary>更新の全通知段階と復元要求を、欠落した翻訳で画面例外にしません。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdatePhasesAndRollbackRequestHaveDisplayText(bool japanese)
    {
        ExecutionProgressViewModel model = new();
        model.Start(new(japanese));
        foreach (Contracts.Updates.EUpdatePhase phase in Enum.GetValues<Contracts.Updates.EUpdatePhase>())
        {
            model.Report(new("update-" + phase, 0, 1, "fixture"));
            Assert.False(string.IsNullOrWhiteSpace(model.Stage));
        }
        model.Report(new("update-restoring", 0, 1, "fixture"));
        string requested = model.Stage;
        model.Report(new("update-" + Contracts.Updates.EUpdatePhase.Restoring, 0, 1, "fixture"));
        Assert.Equal(requested, model.Stage);
    }

    /// <summary>確認後に別画面へ移り、実処理の完了前に成功表示を出しません。</summary>
    /// <returns>実AXAMLと非同期実行の検証です。</returns>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task ExecutionUsesSeparatePageWithLiveExpandableDetails(int language, bool fail)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(async () =>
        {
            TaskCompletionSource<WizardResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IProgress<OperationProgress>? updates = null;
            InstallationViewModel settings = new() { Confirmed = true };
            WizardViewModel model = new(settings, new DiscoveryViewModel(settings), new TestInteraction(), (_, progress, token) =>
            {
                updates = progress;
                return completion.Task.WaitAsync(token);
            }) { LanguageIndex = language, Page = EWizardPage.Review, OperationIndex = (int)EInstallerOperation.Update };
            settings.Confirmed = true;
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Task running = model.NextCommand.ExecuteAsync(null);
            try
            {
                Assert.Equal(EWizardPage.Executing, model.Page);
                Assert.False(model.IsReview);
                Assert.True(model.Busy);
                updates!.Report(new("update-transfer", 2, 10, "Sample / Computer 1", 4096, 8192));
                Dispatcher.UIThread.RunJobs();
                ProgressBar bar = window.FindControl<ProgressBar>("ExecutionBar")!;
                Assert.False(bar.IsIndeterminate);
                Assert.Equal(2, bar.Value);
                Assert.Equal(10, bar.Maximum);
                Assert.Equal(4096, window.FindControl<ProgressBar>("TransferBar")!.Value);
                Expander details = window.FindControl<Expander>("ExecutionDetails")!;
                Assert.False(details.IsExpanded);
                Assert.True(details.IsEffectivelyEnabled);
                int entries = model.Execution.Details.Count;
                updates.Report(new("update-transfer", 2, 10, "Sample / Computer 1", 8192, 8192));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(entries, model.Execution.Details.Count);
                Capture(window, "installer-progress-" + language + ".png");
                details.IsExpanded = true;
                updates.Report(new("update-Restoring", 7, 10, "Sample / Computer 1"));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(entries + 1, model.Execution.Details.Count);
                Assert.False(model.Execution.HasTransfer);
                Assert.False(model.NextCommand.CanExecute(null));
                Capture(window, "installer-progress-details-" + language + ".png");
                if (fail)
                {
                    completion.SetException(new IOException("Fixture connection lost."));
                }
                else
                {
                    completion.SetResult(new("Fixture finished", "Fixture details"));
                }
                await running.ConfigureAwait(true);
                Assert.Equal(EWizardPage.Result, model.Page);
                Assert.False(model.Busy);
                Assert.False(model.Installed);
                if (fail)
                {
                    Assert.Contains("Fixture connection lost.", model.ResultDetails, StringComparison.Ordinal);
                }
            }
            finally
            {
                completion.TrySetResult(new("Stopped", ""));
                await running.ConfigureAwait(true);
                window.Close();
            }
            return true;
        }, CancellationToken.None);
    }

    /// <summary>要求された場合だけ実描画を保存します。</summary>
    private static void Capture(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("CLUTCH_CAPTURE_DIRECTORY");
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            using Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
