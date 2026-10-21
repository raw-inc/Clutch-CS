namespace ReArtWord.Contents.BackgroundService.Contracts.Files;

/// <summary>重なるファイル操作の競合時方針です。</summary>
public enum EFileConflictPolicy
{
    /// <summary>実行前に競合として失敗します。</summary>
    Fail,

    /// <summary>先行操作の解放を待ち、前提を再検証します。</summary>
    Wait,
}
