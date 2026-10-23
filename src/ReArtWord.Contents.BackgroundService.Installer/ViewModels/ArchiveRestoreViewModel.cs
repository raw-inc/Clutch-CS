using CommunityToolkit.Mvvm.ComponentModel;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Installer.Services;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>復元するファイル、復元方式、事前確認の結果を保持します。</summary>
internal sealed partial class ArchiveRestoreViewModel : ObservableObject
{
    /// <summary>読み込むZIPのパスです。</summary>
    [ObservableProperty]
    public partial string Path
    {
        get; set;
    } = string.Empty;

    /// <summary>復元先にだけあるWFを残す場合は0、削除する場合は1。</summary>
    [ObservableProperty]
    public partial int ModeIndex
    {
        get; set;
    }

    /// <summary>事前確認したCoreServiceの接続先とInstanceId。</summary>
    internal ObservedTarget? Target
    {
        get; set;
    }

    /// <summary>変更内容と確認トークンを含む復元計画。</summary>
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
