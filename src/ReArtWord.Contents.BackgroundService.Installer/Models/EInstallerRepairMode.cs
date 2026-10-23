namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>自動修復・再配置・アーカイブ復元を排他的に選びます。</summary>
internal enum EInstallerRepairMode
{
    /// <summary>自動修復を行います。</summary>
    Automatic,

    /// <summary>既存の配置を再インストールします。</summary>
    Reinstall,

    /// <summary>共通設定とWFをアーカイブから復元します。</summary>
    Archive,
}
