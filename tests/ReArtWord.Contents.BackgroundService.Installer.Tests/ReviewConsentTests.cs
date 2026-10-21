using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>確認チェックと実行ボタンの連動を、OS操作なしで検証します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class ReviewConsentTests
{
    /// <summary>すべての操作で確認前・確認解除後は押せず、戻った場合も確認し直します。</summary>
    /// <returns>日英の画面操作と実行境界の確認です。</returns>
    [Theory]
    [InlineData((int)EInstallerOperation.Install, 0)]
    [InlineData((int)EInstallerOperation.Install, 1)]
    [InlineData((int)EInstallerOperation.Update, 0)]
    [InlineData((int)EInstallerOperation.Update, 1)]
    [InlineData((int)EInstallerOperation.Repair, 0)]
    [InlineData((int)EInstallerOperation.Repair, 1)]
    [InlineData((int)EInstallerOperation.Uninstall, 0)]
    [InlineData((int)EInstallerOperation.Uninstall, 1)]
    public async Task ReviewButtonRequiresConfirmation(int operation, int language)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(async () =>
        {
            int executions = 0;
            InstallationViewModel settings = new();
            TestInteraction interaction = new();
            WizardViewModel model = new(settings, new(settings), interaction, (_, _, _) =>
            {
                executions++;
                return Task.FromResult(new WizardResult("Completed", string.Empty));
            }) { OperationIndex = operation, LanguageIndex = language, Page = EWizardPage.Review };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            CheckBox consent = window.FindControl<CheckBox>("ReviewConsent")!;
            Button next = window.FindControl<Button>("NextButton")!;
            Assert.False(next.IsEffectivelyEnabled);
            Assert.False(next.Focus());

            // コマンドを直接呼ばれても、既存の確認チェックで実行を拒否する。
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(0, executions);
            Assert.Equal(EWizardPage.Review, model.Page);
            Assert.False(next.IsEffectivelyEnabled);

            Activate(window, consent);
            Assert.True(settings.Confirmed);
            Assert.True(next.IsEffectivelyEnabled);
            model.Busy = true;
            Assert.False(next.IsEffectivelyEnabled);
            model.Busy = false;
            Assert.True(next.IsEffectivelyEnabled);
            Activate(window, consent);
            Assert.False(settings.Confirmed);
            Assert.False(next.IsEffectivelyEnabled);

            Activate(window, consent);
            model.BackCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(settings.Confirmed);
            Assert.NotEqual(EWizardPage.Review, model.Page);
            Assert.True(next.IsEffectivelyEnabled);
            model.Page = EWizardPage.Review;
            Dispatcher.UIThread.RunJobs();
            Assert.False(next.IsEffectivelyEnabled);

            Activate(window, consent);
            Activate(window, next);
            Assert.NotNull(model.NextCommand.ExecutionTask);
            await model.NextCommand.ExecutionTask.ConfigureAwait(true);
            Assert.Equal(1, executions);
            Assert.Equal(EWizardPage.Result, model.Page);

            // 結果画面の「完了」は、確認画面のチェックに依存しない。
            settings.Confirmed = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(next.IsEffectivelyEnabled);
            Activate(window, next);
            await model.NextCommand.ExecutionTask.ConfigureAwait(true);
            Assert.Equal(1, interaction.CloseRequests);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    /// <summary>キーボード入力でチェック切替またはボタン操作を行います。</summary>
    private static void Activate(Window window, Control control)
    {
        Assert.True(control.Focus());
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Dispatcher.UIThread.RunJobs();
    }
}
