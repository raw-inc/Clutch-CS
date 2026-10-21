namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;

/// <summary>予約の待機と、更新開始後の確認状態を区別します。</summary>
public enum EUpdateScheduleState
{
    /// <summary>指定日時と開始可能条件を待っています。</summary>
    Waiting,

    /// <summary>開始意図を永続化し、更新起動を一度試行します。</summary>
    Starting,

    /// <summary>元更新IDの結果を照会しています。</summary>
    Running,

    /// <summary>元更新の結果または開始不能を確認しました。</summary>
    Completed,

    /// <summary>開始前に取消を確定しました。</summary>
    Cancelled,

    /// <summary>開始可能な猶予期間を超過しました。</summary>
    Expired,

    /// <summary>元IDを照会しますが、自動で開始し直しません。</summary>
    Unconfirmed,
}
