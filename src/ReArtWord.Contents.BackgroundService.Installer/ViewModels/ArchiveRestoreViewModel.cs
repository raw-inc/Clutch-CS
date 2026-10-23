using CommunityToolkit.Mvvm.ComponentModel;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Installer.Services;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>復元するファイル、既存WFの扱いと確認済み計画を保持します。</summary>
internal sealed partial class ArchiveRestoreViewModel : ObservableObject
{
    /// <summary>読み込むZIPのパスです。</summary>
    [ObservableProperty]
    public partial string Path
    {
        get; set;
    } = string.Empty;

    /// <summary>0は既存WFを残し、1は取得元にそろえます。</summary>
    [ObservableProperty]
    public partial int ModeIndex
    {
        get; set;
    }

    /// <summary>確認時点で固定した対象実体です。</summary>
    internal ObservedTarget? Target
    {
        get; set;
    }

    /// <summary>変更内容と確認値を含む元計画です。</summary>
    internal ArchiveImportPreview? Preview
    {
        get; set;
    }

    /// <summary>画面で選んだ復元方式です。</summary>
    internal EArchiveImportMode Mode => ModeIndex switch
    {
        0 => EArchiveImportMode.Merge,
        1 => EArchiveImportMode.Replace,
        _ => throw new InvalidOperationException("Select an archive import mode."),
    };
}
