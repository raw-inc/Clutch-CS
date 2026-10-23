namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>処理中の対象の扱いと更新順序です。</summary>
internal enum EInstallerUpdateMode
{
    /// <summary>処理中の対象は更新せず、ほかの対象を更新します。</summary>
    SkipActive,

    /// <summary>選択した対象の処理を停止して更新します。</summary>
    StopActive,

    /// <summary>復帰を確認しながら一台ずつ更新します。</summary>
    Fleet,
}
