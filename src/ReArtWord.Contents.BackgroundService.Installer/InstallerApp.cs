using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;

namespace ReArtWord.Contents.BackgroundService.Installer;

/// <summary>各OSに共通のFluent画面を構成します。</summary>
internal sealed partial class InstallerApp : Application
{
    /// <summary>文字サイズとテーマを設定します。</summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>独立した対話windowを表示します。</summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            InstallerWindow window = new();
            InstallationViewModel settings = new();
            window.DataContext = new WizardViewModel(settings, new DiscoveryViewModel(settings), new DesktopInteraction(window));
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
