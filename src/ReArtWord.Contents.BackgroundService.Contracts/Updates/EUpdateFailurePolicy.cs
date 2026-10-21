namespace ReArtWord.Contents.BackgroundService.Contracts.Updates;

/// <summary>開始前に選択するクラスタ更新失敗時の方針です。</summary>
public enum EUpdateFailurePolicy
{
    /// <summary>互換性を確認した新旧リリースの混在を維持します。</summary>
    KeepCompatibleMixed,

    /// <summary>更新成功を確認したPeerを旧リリースへ戻します。</summary>
    RollbackSuccessful,
}
