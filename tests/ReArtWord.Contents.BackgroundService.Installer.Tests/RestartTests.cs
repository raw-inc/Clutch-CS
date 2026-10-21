using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>実OSへ再起動を要求せず、終了通知と完了画面の操作を検証します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class RestartTests
{
    /// <summary>再起動要求の待機中に来るOS終了通知を、処理中ガードで拒否しません。</summary>
    /// <returns>headless windowでの終了通知の検証完了です。</returns>
    [Fact]
    public async Task OsShutdownDuringRestartRequestIsNotCancelledByInstaller()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(async () =>
        {
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TestInteraction interaction = new() { RestartBehavior = token => completion.Task.WaitAsync(token) };
            InstallationViewModel settings = new();
            WizardViewModel model = new(settings, new DiscoveryViewModel(settings), interaction) { Page = EWizardPage.Result, Installed = true, RestartConfirmed = true };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Task request = model.RestartCommand.ExecuteAsync(null);
            try
            {
                Assert.True(model.Busy);
                Assert.Equal(1, interaction.RestartRequests);
                RequestClose(window, WindowCloseReason.WindowClosing);
                Assert.True(window.IsVisible);
                RequestClose(window, WindowCloseReason.OSShutdown);
                Assert.False(window.IsVisible);
            }
            finally
            {
                completion.TrySetResult();
                await request.ConfigureAwait(true);
                window.Close();
            }
            return true;
        }, CancellationToken.None);
    }

    /// <summary>導入処理中の終了保護は維持します。</summary>
    /// <returns>副作用処理の途中で閉じない検証です。</returns>
    [Fact]
    public async Task OsShutdownIsStillBlockedWhileInstallationIsRunning()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(() =>
        {
            InstallationViewModel settings = new();
            WizardViewModel model = new(settings, new DiscoveryViewModel(settings), new TestInteraction()) { Page = EWizardPage.Review, Busy = true };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            RequestClose(window, WindowCloseReason.OSShutdown);
            Assert.True(window.IsVisible);
            Assert.Equal(model.Text["BusyClose"], model.Error);
            model.Busy = false;
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    /// <summary>未確認時は操作できず、要求失敗時は完了画面に理由を表示します。</summary>
    /// <returns>実AXAMLの操作と失敗表示の検証です。</returns>
    [Fact]
    public async Task RestartRequiresConsentAndReportsFailure()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(async () =>
        {
            string failure = "Restart was denied by the test operating system.";
            TestInteraction interaction = new() { RestartBehavior = _ => Task.FromException(new InvalidDataException(failure)) };
            InstallationViewModel settings = new();
            WizardViewModel model = new(settings, new DiscoveryViewModel(settings), interaction) { Page = EWizardPage.Result, Installed = true };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Button restart = window.FindControl<Button>("RestartNowButton")!;
            Assert.False(restart.IsEffectivelyEnabled);
            await model.RestartCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(0, interaction.RestartRequests);
            window.FindControl<CheckBox>("RestartConsent")!.IsChecked = true;
            Assert.True(restart.IsEffectivelyEnabled);
            Assert.True(restart.Focus());
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            await model.RestartCommand.ExecutionTask!.ConfigureAwait(true);
            Assert.Equal(1, interaction.RestartRequests);
            Assert.Equal(failure, model.Error);
            Assert.False(model.Busy);
            Assert.True(window.IsVisible);
            Assert.Equal(0, interaction.CloseRequests);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    /// <summary>後で再起動は、チェックの有無にかかわらずOS要求を出しません。</summary>
    /// <returns>対話終了だけを行う検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartLaterClosesWithoutRequestingRestart(bool confirmed)
    {
        InstallationViewModel settings = new();
        TestInteraction interaction = new();
        WizardViewModel model = new(settings, new DiscoveryViewModel(settings), interaction) { Page = EWizardPage.Result, Installed = true, RestartConfirmed = confirmed };
        await model.NextCommand.ExecuteAsync(null);
        Assert.Equal(0, interaction.RestartRequests);
        Assert.Equal(1, interaction.CloseRequests);
    }

    /// <summary>AvaloniaのOS終了通知入口はinternalのため、同じ入口をheadless windowに限って呼び出します。</summary>
    private static void RequestClose(Window window, WindowCloseReason reason)
    {
        System.Reflection.MethodInfo close = typeof(Window).GetMethod("CloseCore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Avalonia window close entry point was not found.");
        close.Invoke(window, [reason, false, false]);
    }
}
