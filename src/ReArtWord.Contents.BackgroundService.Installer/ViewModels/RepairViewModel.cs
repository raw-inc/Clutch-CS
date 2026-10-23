using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReArtWord.Contents.BackgroundService.Installer.Models;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>修復方式の排他的な選択と、アーカイブ復元の入力を保持します。</summary>
internal sealed partial class RepairViewModel : ObservableObject
{
    /// <summary>選択した修復方式です。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Reinstall)), NotifyPropertyChangedFor(nameof(Automatic)), NotifyPropertyChangedFor(nameof(RestoreArchive))]
    public partial EInstallerRepairMode Mode
    {
        get; set;
    }

    /// <summary>自動修復を選択しているかです。</summary>
    public bool Automatic => Mode == EInstallerRepairMode.Automatic;

    /// <summary>再配置を選択しているかです。</summary>
    public bool Reinstall => Mode == EInstallerRepairMode.Reinstall;

    /// <summary>アーカイブ復元を選択しているかです。</summary>
    public bool RestoreArchive => Mode == EInstallerRepairMode.Archive;

    /// <summary>アーカイブの入力と確認済み内容です。</summary>
    public ArchiveRestoreViewModel Archive
    {
        get;
    } = new();

    /// <summary>選択解除の通知でほかの選択を上書きしません。</summary>
    [RelayCommand]
    private void ChooseMode(EInstallerRepairMode mode) => Mode = mode;
}
