namespace ReArtWord.Contents.BackgroundService.Contracts.Files;

/// <summary>一台内の途中失敗時に既存内容を戻すかを指定します。</summary>
public enum EFileInternalFailurePolicy
{
    /// <summary>途中状態を保持します。</summary>
    Keep,

    /// <summary>実行前に退避した状態へ復元します。</summary>
    Restore,
}
