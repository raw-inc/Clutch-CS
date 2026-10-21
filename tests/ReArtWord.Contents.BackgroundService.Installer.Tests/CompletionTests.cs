using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>完了画面に、導入結果に対応したURLを表示します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class CompletionTests
{
    /// <summary>既定・指定portと既存利用を反映し、選択・コピーできるURLを表示します。</summary>
    /// <returns>日英での表示と起動操作の確認です。</returns>
    [Theory]
    [InlineData(0, 5081, false)]
    [InlineData(1, 5081, false)]
    [InlineData(0, 5182, false)]
    [InlineData(1, 5182, false)]
    [InlineData(0, 5182, true)]
    [InlineData(1, 5182, true)]
    public async Task CompletionShowsActualClientUrl(int language, int port, bool reused)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(async () =>
        {
            InstallationViewModel settings = new() { Confirmed = true };
            WebClientInstallation client = new(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "completion-client"), "client.exe", port, true);
            if (reused)
            {
                settings.Components.Install = false;
                settings.Components.Existing = client;
            }
            TestInteraction interaction = new();
            WizardViewModel model = new(settings, new(settings), interaction, (state, _, _) =>
            {
                state.Installed = true;
                return Task.FromResult(new WizardResult(state.Text("Completed.", "完了しました。"), string.Empty, client));
            }) { LanguageIndex = language, Page = EWizardPage.Review };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextBox address = window.FindControl<TextBox>("CompletedClientUrl")!;
            Assert.False(address.IsEffectivelyVisible);
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EWizardPage.Result, model.Page);
            Assert.True(address.IsEffectivelyVisible);
            Assert.True(address.IsReadOnly);
            string expected = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
            Assert.Equal(expected, address.Text);
            address.SelectAll();
            Assert.Equal(expected, address.SelectedText);

            // 完了後の案内は、入力欄ではなく確定した導入結果に従う。
            settings.Components.Port = WebClientSettings.DefaultPort;
            settings.Components.Existing = null;
            settings.Components.Install = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expected, address.Text);
            Button open = window.FindControl<Button>("OpenClientButton")!;
            Assert.True(open.IsEffectivelyVisible);
            Assert.True(open.Focus());
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            await model.OpenClientCommand.ExecutionTask!.ConfigureAwait(true);
            Assert.Equal(client.Root, interaction.OpenedClientRoot);
            Capture(window, $"installer-completed-client-{language}-{port}-{reused}.png");
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    /// <summary>未導入や失敗の結果を、利用可能なURLとして案内しません。</summary>
    /// <returns>結果に応じた表示範囲の確認です。</returns>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task NoClientOrFailedInstallDoesNotShowUrl(bool succeeded, bool hasClient)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(async () =>
        {
            InstallationViewModel settings = new() { Confirmed = true };
            WebClientInstallation? client = null;
            if (hasClient)
            {
                client = new(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "completion-client"), "client.exe", WebClientSettings.DefaultPort, true);
            }
            WizardViewModel model = new(settings, new(settings), new TestInteraction(), (state, _, _) =>
            {
                state.Installed = succeeded;
                return Task.FromResult(new WizardResult("Fixture result", string.Empty, client));
            }) { Page = EWizardPage.Review };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EWizardPage.Result, model.Page);
            Assert.False(window.FindControl<TextBox>("CompletedClientUrl")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("OpenClientButton")!.IsEffectivelyVisible);
            window.Close();
            return true;
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
