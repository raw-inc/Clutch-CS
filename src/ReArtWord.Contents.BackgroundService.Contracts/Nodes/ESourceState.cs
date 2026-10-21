namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>一件の結果と分離した継続待受自体の状態です。</summary>
public enum ESourceState
{
    /// <summary>開始処理中です。</summary>
    Starting,

    /// <summary>入力を受け付けています。</summary>
    Listening,

    /// <summary>必要な対象・接続が一時利用できません。</summary>
    Unavailable,

    /// <summary>予定された正常終了を完了しました。</summary>
    Completed,

    /// <summary>取消による停止を確認しました。</summary>
    Cancelled,

    /// <summary>継続不能な失敗で終了しました。</summary>
    Failed,
}
