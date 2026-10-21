namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>復元前の未保存編集に対する人の選択です。</summary>
public enum ESnapshotDraftAction
{
    /// <summary>別操作で保存済みとなった内容を確認します。</summary>
    ConfirmSaved,

    /// <summary>当該範囲の下書きを現在の保存版へ戻します。</summary>
    Discard,

    /// <summary>復元の開始を取りやめます。</summary>
    CancelRestore,
}
