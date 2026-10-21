namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>利用者が選択する保守動作です。</summary>
internal enum EInstallerOperation
{
    /// <summary>初回導入です。</summary>
    Install,

    /// <summary>分散更新です。</summary>
    Update,

    /// <summary>修復です。</summary>
    Repair,

    /// <summary>削除です。</summary>
    Uninstall,
}
