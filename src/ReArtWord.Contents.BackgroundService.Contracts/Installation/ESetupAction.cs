namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>配置管理で実施する操作です。</summary>
public enum ESetupAction
{
    /// <summary>新しい配置を作成します。</summary>
    Install,

    /// <summary>配布内容から配置を再作成します。</summary>
    Reinstall,

    /// <summary>所有する配置と自動起動登録を削除します。</summary>
    Uninstall,
}
