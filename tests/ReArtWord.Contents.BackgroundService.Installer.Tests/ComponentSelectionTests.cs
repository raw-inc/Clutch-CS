using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>管理画面の有無とportを、実AXAMLの操作から検証します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class ComponentSelectionTests
{
    /// <summary>導入しない場合はportと外観の入力を省き、最終確認に反映します。</summary>
    /// <returns>入力・遷移・確認の検証です。</returns>
    [Fact]
    public async Task OptionalClientControlsPortAndAppearancePages()
    {
        using SetupFixture fixture = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(async () =>
        {
            await fixture.InitializeAsync().ConfigureAwait(true);
            InstallationViewModel settings = new() { Package = fixture.Settings.PackageRoot, Root = fixture.Settings.Root, Advanced = true, PerUser = true, Accepted = true, LicenseRead = true };
            settings.Networks.Add(fixture.Settings.Network);
            WizardViewModel model = new(settings, new(settings), new TestInteraction()) { LanguageIndex = 1, Page = EWizardPage.Options };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            CheckBox install = window.FindControl<CheckBox>("InstallWebClient")!;
            NumericUpDown port = window.FindControl<NumericUpDown>("WebClientPort")!;
            Assert.True(install.IsChecked);
            Assert.Equal(WebClientSettings.DefaultPort, port.Value);
            Assert.True(port.IsEffectivelyVisible);
            port.Value = 5082;
            Assert.Equal(5082, settings.Components.Capture(true).Port);
            Capture(window, "installer-web-client-options.png");
            install.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(port.IsEffectivelyVisible);
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(EWizardPage.Advanced, model.Page);
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(string.Empty, model.Error);
            Assert.Equal(EWizardPage.Review, model.Page);
            Assert.Contains("Web クライアント：インストールしない", model.ReviewText, StringComparison.Ordinal);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    /// <summary>既存clientのportと配置を表示し、重複導入の選択を無効にします。</summary>
    /// <returns>既存利用時の画面と確認一覧の検証です。</returns>
    [Fact]
    public async Task ExistingClientSkipsInstallationAndPreservesActualPort()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(() =>
        {
            InstallationViewModel settings = new() { Accepted = true, LicenseRead = true };
            DiscoveryViewModel discovery = new(settings);
            discovery.SelectedNetwork = discovery.Networks.Single(network => string.Equals(network.Binding.BindAddress, "127.0.0.1", StringComparison.Ordinal));
            settings.Networks.Add(discovery.SelectedNetwork.Binding);
            settings.Components.Existing = new(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "existing-client"), "client.exe", 5182, true);
            settings.Components.Install = false;
            WizardViewModel model = new(settings, discovery, new TestInteraction()) { LanguageIndex = 1, Page = EWizardPage.Options };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<CheckBox>("InstallWebClient")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<NumericUpDown>("WebClientPort")!.IsEffectivelyVisible);
            WebClientSettings captured = settings.Components.Capture(true);
            Assert.False(captured.Install);
            Assert.Equal(5182, captured.Port);
            Assert.Equal(settings.Components.Existing.Root, captured.ExistingRoot);
            string summary = WizardSummary.Create(settings);
            Assert.Contains("インストール済みのものを使用", summary, StringComparison.Ordinal);
            Assert.Contains("5182", summary, StringComparison.Ordinal);
            Capture(window, "installer-web-client-existing.png");
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    /// <summary>clientだけの削除はネットワーク走査せず確認と明示実行へ進みます。</summary>
    /// <returns>クラスターへ触れない画面遷移の確認です。</returns>
    [Fact]
    public async Task ClientOnlyRemovalNeverScansClusters()
    {
        using SetupFixture fixture = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(async () =>
        {
            await fixture.InitializeAsync().ConfigureAwait(true);
            Directory.CreateDirectory(fixture.Platform.ClientRoot);
            WebClientInstallation existing = new(Guid.NewGuid(), fixture.Platform.ClientRoot, "client.exe", WebClientSettings.DefaultPort, true);
            await InstallationFiles.WriteAsync(Path.Combine(existing.Root, WebClientCatalog.RecordName), existing).ConfigureAwait(true);
            InstallationViewModel settings = new();
            settings.Components.Existing = existing;
            settings.Components.RemoveService = false;
            bool executed = false;
            WizardViewModel model = new(settings, new(settings, (_, _) => throw new InvalidOperationException("The network must not be scanned.")), new TestInteraction(), (state, _, _) =>
            {
                Assert.Empty(state.Selected);
                executed = true;
                return Task.FromResult(new WizardResult("Fixture completed", string.Empty));
            }) { LanguageIndex = 1, OperationIndex = (int)EInstallerOperation.Uninstall, Page = EWizardPage.Components };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<Button>("NextButton")!.IsEffectivelyEnabled);
            window.FindControl<CheckBox>("RemoveWebClient")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Button>("NextButton")!.IsEffectivelyEnabled);
            Capture(window, "installer-remove-components.png");
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(EWizardPage.Maintenance, model.Page);
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(EWizardPage.Review, model.Page);
            Assert.Contains(existing.Root, model.ReviewText, StringComparison.Ordinal);
            Assert.Contains("Web クライアントを削除する", model.ReviewText, StringComparison.Ordinal);
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.False(executed);
            settings.Confirmed = true;
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.True(executed);
            Assert.Equal(EWizardPage.Result, model.Page);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    /// <summary>portの範囲外と小数を拒否し、非導入時の不要な入力は無視します。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(1.5)]
    public void InvalidPortOnlyBlocksNewInstallation(double port)
    {
        ComponentsViewModel model = new() { Port = (decimal)port };
        Assert.Throws<InvalidDataException>(() => model.Capture(true));
        model.Install = false;
        Assert.False(model.Capture(true).Install);
    }

    /// <summary>指定された場合だけ同じAXAMLの描画を保存します。</summary>
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
