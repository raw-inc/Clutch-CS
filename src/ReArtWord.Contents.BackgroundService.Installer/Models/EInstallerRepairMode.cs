namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>修復画面で選択する実行方法。</summary>
internal enum EInstallerRepairMode
{
    /// <summary>自動修復を行います。</summary>
    Automatic,

    /// <summary>既存の配置を再インストールします。</summary>
    Reinstall,

    /// <summary>共通設定とWFをアーカイブから復元します。</summary>
    Archive,
}
