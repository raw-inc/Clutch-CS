using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;

namespace ReArtWord.Contents.BackgroundService.Installer.Views;

/// <summary>表示プレビューの画像を読み込み、画面を閉じたら解放します。</summary>
internal sealed partial class AppearancePreview : UserControl
{
    private AppearanceViewModel? settings;
    private Bitmap? icon;
    private Bitmap? favicon;
    private bool attached;
    private static int PreviewImageWidth => 128;

    /// <summary>プレビューの表示を構成します。</summary>
    public AppearancePreview() => InitializeComponent();

    /// <summary>表示する設定が変わった場合は購読先を更新します。</summary>
    protected override void OnDataContextChanged(EventArgs args)
    {
        base.OnDataContextChanged(args);
        if (attached)
        {
            ObserveSettings();
        }
    }

    /// <summary>画面を表示したときに現在の画像を読み込みます。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        attached = true;
        ObserveSettings();
    }

    /// <summary>購読と画像を解放します。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        attached = false;
        if (settings is not null)
        {
            settings.PropertyChanged -= OnSettingsChanged;
        }
        settings = null;
        ShowImage(ref icon, HeaderImage, DefaultHeaderIcon, null);
        ShowImage(ref favicon, TabImage, DefaultTabIcon, null);
        base.OnDetachedFromVisualTree(args);
    }

    /// <summary>画像パスの変更を画面に反映します。</summary>
    private void ObserveSettings()
    {
        if (settings is not null)
        {
            settings.PropertyChanged -= OnSettingsChanged;
        }
        settings = DataContext as AppearanceViewModel;
        if (settings is not null)
        {
            settings.PropertyChanged += OnSettingsChanged;
        }
        ShowImage(ref icon, HeaderImage, DefaultHeaderIcon, settings?.Icon);
        ShowImage(ref favicon, TabImage, DefaultTabIcon, TabIconPath());
    }

    /// <summary>変更された画像だけを読み直します。</summary>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.Equals(args.PropertyName, nameof(AppearanceViewModel.Icon), StringComparison.Ordinal))
        {
            ShowImage(ref icon, HeaderImage, DefaultHeaderIcon, settings?.Icon);
        }
        if (string.Equals(args.PropertyName, nameof(AppearanceViewModel.Favicon), StringComparison.Ordinal)
            || string.Equals(args.PropertyName, nameof(AppearanceViewModel.Icon), StringComparison.Ordinal))
        {
            ShowImage(ref favicon, TabImage, DefaultTabIcon, TabIconPath());
        }
    }

    /// <summary>タブ画像が未指定なら、ClientHostと同じくヘッダー画像を使用します。</summary>
    /// <returns>既定表示を含む画像の選択です。</returns>
    private string? TabIconPath()
    {
        if (string.IsNullOrWhiteSpace(settings?.Favicon))
        {
            return settings?.Icon;
        }
        return settings.Favicon;
    }

    /// <summary>画像を差し替え、読み込めない場合は既定表示に戻します。</summary>
    private static void ShowImage(ref Bitmap? current, Image image, Control fallback, string? path)
    {
        Bitmap? next = ReadBitmap(path);
        Bitmap? previous = current;
        current = next;
        image.Source = next;
        fallback.IsVisible = next is null;
        previous?.Dispose();
    }

    /// <summary>入力途中のパスで画面を止めず、読み込める画像だけを表示します。</summary>
    /// <returns>縮小した画像、またはnullです。</returns>
    private static Bitmap? ReadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            using MemoryStream stream = new(InstallationImages.Read(path.Trim()), false);
            return Bitmap.DecodeToWidth(stream, PreviewImageWidth);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
