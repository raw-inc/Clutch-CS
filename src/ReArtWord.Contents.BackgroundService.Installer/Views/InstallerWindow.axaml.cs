using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;

namespace ReArtWord.Contents.BackgroundService.Installer.Views;

/// <summary>処理途中のwindow終了を防ぎ、導入完了後のOS終了通知を通します。</summary>
internal sealed partial class InstallerWindow : Window
{
    /// <summary>表示とwindow寿命の境界だけを構成します。</summary>
    public InstallerWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // 再起動要求の応答待ち中でも、導入完了後のOS終了を拒否しません。
        Closing += (_, args) =>
        {
            if (DataContext is WizardViewModel { Busy: true } model &&
                !(args.CloseReason == WindowCloseReason.OSShutdown && model.IsResult && model.Installed))
            {
                args.Cancel = true;
                model.Error = model.Text["BusyClose"];
            }
        };
    }

    /// <summary>実際に表示された範囲だけから末尾到達をViewModelへ通知します。</summary>
    private void OnLicenseScrollChanged(object? sender, ScrollChangedEventArgs args)
    {
        if (sender is ScrollViewer { IsEffectivelyVisible: true } viewer &&
            DataContext is WizardViewModel { IsLicense: true, Settings.LicenseRead: false } model &&
            0 < viewer.Viewport.Height && 0 < viewer.Extent.Height &&
            Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height) <= viewer.Offset.Y)
        {
            model.Settings.ReadLicenseCommand.Execute(null);
        }
    }
}
