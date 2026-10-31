using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>実AXAMLのbinding、言語、同意と画面遷移を確認します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class WizardTests
{
    /// <summary>同意なしでは先へ進まず、高度な設定だけを必要に応じて表示します。</summary>
    /// <returns>UI session上の検証完了です。</returns>
    [Fact]
    public async Task AxamlBindingsConsentNavigationAndDarkTheme()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(async () =>
        {
            InstallationViewModel settings = new();
            int scans = 0;
            DiscoveryViewModel discovery = new(settings, (binding, _) =>
            {
                Assert.Equal("127.0.0.1", binding.BindAddress);
                scans++;
                return Task.FromResult<IReadOnlyList<ObservedTarget>>([]);
            });
            WizardViewModel model = new(settings, discovery, new TestInteraction());
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.Equal("Welcome to Clutch-CS", window.FindControl<TextBlock>("PageHeading")!.Text);
            model.LanguageIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Clutch-CS へようこそ", window.FindControl<TextBlock>("PageHeading")!.Text);
            Capture(window, "installer-welcome.png");
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(EWizardPage.Operation, model.Page);
            Capture(window, "installer-operation.png");
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(EWizardPage.License, model.Page);
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(EWizardPage.License, model.Page);
            Assert.False(window.FindControl<Button>("NextButton")!.IsEffectivelyEnabled);
            window.FindControl<ScrollViewer>("LicenseScroll")!.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.LicenseRead);
            settings.Accepted = true;
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(EWizardPage.Network, model.Page);
            Assert.Equal(string.Empty, model.Error);
            Assert.Null(model.Discovery.SelectedNetwork);
            Assert.Empty(settings.Networks);
            Assert.False(window.FindControl<Button>("NextButton")!.IsEffectivelyEnabled);
            Capture(window, "installer-network.png");
            model.Discovery.SelectedNetwork = model.Discovery.Networks.Single(network => string.Equals(network.Binding.BindAddress, "127.0.0.1", StringComparison.Ordinal));
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(EWizardPage.Targets, model.Page);
            Assert.Equal(1, scans);
            Assert.Single(settings.Networks);
            Capture(window, "installer-cluster.png");
            model.Page = EWizardPage.Identity;
            Capture(window, "installer-identity.png");
            model.ReviewText = WizardSummary.Create(settings);
            model.Page = EWizardPage.Review;
            Capture(window, "installer-review.png");
            settings.Cluster = "sample-cluster";
            Assert.EndsWith("-sample-cluster-peer", settings.Peer, StringComparison.Ordinal);
            settings.Host = "test-host";
            Assert.Equal("test-host-sample-cluster-peer", settings.Peer);
            settings.Peer = "custom-peer";
            settings.Host = "renamed-host";
            settings.Cluster = "renamed-cluster";
            Assert.Equal("custom-peer", settings.Peer);
            model.Page = EWizardPage.Options;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<RadioButton>("RecommendedSettingsOption")!.IsChecked);
            Assert.False(settings.Advanced);
            Capture(window, "installer-options.png");
            Activate(window, model.Text["Advanced"]);
            Assert.True(settings.Advanced);
            Assert.False(window.FindControl<RadioButton>("RecommendedSettingsOption")!.IsChecked);
            Activate(window, model.Text["Recommended"]);
            Assert.False(settings.Advanced);
            Activate(window, model.Text["Advanced"]);
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(EWizardPage.Advanced, model.Page);
            Capture(window, "installer-advanced.png");
            model.Page = EWizardPage.Appearance;
            Capture(window, "installer-appearance.png");
            model.OperationIndex = (int)EInstallerOperation.Repair;
            model.Page = EWizardPage.Maintenance;
            Dispatcher.UIThread.RunJobs();
            string reinstallLabel = model.Text["Reinstall"];
            Activate(window, reinstallLabel);
            Assert.True(settings.Repair.Reinstall);
            string resyncLabel = model.Text["Resync"];
            Activate(window, resyncLabel);
            Assert.False(settings.KeepDatabase);
            string automaticLabel = model.Text["AutoRepair"];
            Activate(window, automaticLabel);
            Assert.False(settings.Repair.Reinstall);
            model.OperationIndex = (int)EInstallerOperation.Uninstall;
            model.Page = EWizardPage.Review;
            settings.Confirmed = false;
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(EWizardPage.Review, model.Page);
            Assert.Equal(model.Text["ConfirmError"], model.Error);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    /// <summary>スクロールの途中・未同意・実行中は進めず、末尾到達後にだけ同意できます。</summary>
    /// <returns>両言語での実画面の検証完了です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task LicenseRequiresEndOfDocumentAndExplicitConsent(int language)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(async () =>
        {
            InstallationViewModel settings = new();
            WizardViewModel model = new(settings, new DiscoveryViewModel(settings), new TestInteraction()) { LanguageIndex = language, Page = EWizardPage.License };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            ScrollViewer scroll = window.FindControl<ScrollViewer>("LicenseScroll")!;
            CheckBox consent = window.FindControl<CheckBox>("LicenseConsent")!;
            Button next = window.FindControl<Button>("NextButton")!;
            Assert.True(scroll.Viewport.Height < scroll.Extent.Height);
            Assert.False(settings.LicenseRead);
            Assert.False(consent.IsEffectivelyEnabled);
            Assert.False(next.IsEffectivelyEnabled);
            Capture(window, $"installer-license-{language}.png");

            settings.Accepted = true;
            Assert.False(next.IsEffectivelyEnabled);
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(EWizardPage.License, model.Page);
            settings.Accepted = false;
            scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height / 2);
            window.Height = window.MinHeight;
            window.Width = window.MinWidth;
            Dispatcher.UIThread.RunJobs();
            Assert.False(settings.LicenseRead);
            Assert.False(consent.IsEffectivelyEnabled);

            scroll.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.LicenseRead);
            Assert.True(consent.IsEffectivelyEnabled);
            Assert.False(next.IsEffectivelyEnabled);
            consent.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.Accepted);
            Assert.True(next.IsEffectivelyEnabled);
            Assert.True(model.NextCommand.CanExecute(null));
            Capture(window, $"installer-license-end-{language}.png");

            model.Busy = true;
            Assert.False(next.IsEffectivelyEnabled);
            model.Busy = false;
            consent.IsChecked = false;
            Assert.False(next.IsEffectivelyEnabled);
            consent.IsChecked = true;
            model.BackCommand.Execute(null);
            await model.NextCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(EWizardPage.License, model.Page);
            Assert.True(next.IsEffectivelyEnabled);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    /// <summary>参加先は候補だけで選び、新規作成時だけ名前とportを入力します。</summary>
    /// <returns>実AXAMLでの分岐と接続先の検証完了です。</returns>
    [Fact]
    public async Task ExistingClusterUsesItsPortAndHidesNewClusterFields()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(() =>
        {
            InstallationViewModel settings = new() { Accepted = true, LicenseRead = true };
            DiscoveryViewModel discovery = new(settings);
            discovery.SelectedNetwork = discovery.Networks.Single(network => string.Equals(network.Binding.BindAddress, "127.0.0.1", StringComparison.Ordinal));
            ClusterChoice candidate = new("Sample", 50123);
            discovery.Clusters.Add(candidate);
            discovery.ClusterCount = discovery.Clusters.Count;
            discovery.SearchCompleted = true;
            discovery.SelectedCluster = candidate;
            WizardViewModel model = new(settings, discovery, new TestInteraction()) { LanguageIndex = 1, Page = EWizardPage.Targets };
            InstallerWindow window = new() { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            RadioButton join = window.FindControl<RadioButton>("JoinClusterOption")!;
            RadioButton create = window.FindControl<RadioButton>("NewClusterOption")!;
            Assert.True(join.Bounds.Y < create.Bounds.Y);
            Assert.True(join.IsChecked);
            Assert.False(create.IsChecked);
            Assert.Equal(candidate, window.FindControl<ComboBox>("JoinClusterSelector")!.SelectedItem);
            Assert.False(settings.NewCluster);
            Assert.False(window.FindControl<TextBox>("NewClusterName")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<NumericUpDown>("NewClusterPort")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<ComboBox>("JoinClusterSelector")!.IsEffectivelyVisible);
            discovery.CaptureTargets();
            Assert.Equal(candidate.Port, settings.Settings().Network.Port);
            Capture(window, "installer-join-cluster.png");
            Activate(window, model.Text["NewCluster"]);
            settings.Port = candidate.Port + 1;
            Assert.Equal(candidate.Port + 1, settings.Settings().Network.Port);
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    /// <summary>実際のキー入力でradioのcommandと選択表示を同時に確認します。</summary>
    private static void Activate(Window window, string label)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.GetVisualDescendants().OfType<RadioButton>().Single(button => Equals(button.Content, label)).Focus());
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>要求された場合だけ実描画の確認用画像を保存します。</summary>
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
