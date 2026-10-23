using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReArtWord.Contents.BackgroundService.Installer.Models;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>修復方式の選択と、アーカイブ復元の入力内容を保持します。</summary>
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

    /// <summary>再インストールが選択されているか。</summary>
    public bool Reinstall => Mode == EInstallerRepairMode.Reinstall;

    /// <summary>アーカイブ復元を選択しているかです。</summary>
    public bool RestoreArchive => Mode == EInstallerRepairMode.Archive;

    /// <summary>アーカイブの入力と確認済み内容です。</summary>
    public ArchiveRestoreViewModel Archive
    {
        get;
    } = new();

    /// <summary>選択された修復方式を反映します。</summary>
    [RelayCommand]
    private void ChooseMode(EInstallerRepairMode mode) => Mode = mode;
}
