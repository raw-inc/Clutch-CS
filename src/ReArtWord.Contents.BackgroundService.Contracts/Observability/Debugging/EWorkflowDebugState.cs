namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;

/// <summary>一時デバッグの受付と取得状態を区別します。</summary>
public enum EWorkflowDebugState
{
    /// <summary>参加実体へ予約を配布しています。</summary>
    Preparing,

    /// <summary>次の一件を待っています。</summary>
    Armed,

    /// <summary>採用した一件を記録しています。</summary>
    Recording,

    /// <summary>一件の記録が完了しました。</summary>
    Completed,

    /// <summary>記録の予約または取得を停止しました。</summary>
    Cancelled,

    /// <summary>予約または参加実体の有効範囲を外れました。</summary>
    Expired,

    /// <summary>予約の確認が完了していません。</summary>
    Unconfirmed,
}
