namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>予定時刻に対象PCで開始できない回の扱いです。</summary>
public enum EScheduleMissedPolicy
{
    /// <summary>対象PCが使用可能になった時点で開始します。</summary>
    Delay,

    /// <summary>開始できない回を見送ります。</summary>
    Skip,

    /// <summary>許容した時間内に使用可能になった場合だけ開始します。</summary>
    Within,
}
