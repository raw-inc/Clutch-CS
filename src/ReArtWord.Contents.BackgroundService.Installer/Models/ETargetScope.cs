namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>確認画面で固定する対象の選択方式です。</summary>
internal enum ETargetScope
{
    /// <summary>観測した全対象です。</summary>
    All,

    /// <summary>一つのクラスタです。</summary>
    Cluster,

    /// <summary>個別にチェックしたピアです。</summary>
    Peers,
}
