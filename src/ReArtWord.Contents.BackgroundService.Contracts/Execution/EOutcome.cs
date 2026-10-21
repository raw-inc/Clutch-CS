namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>処理の実施状況と確認できた最終結果です。</summary>
public enum EOutcome
{
    /// <summary>必要な成功条件を確認しました。</summary>
    Succeeded,

    /// <summary>実施した処理の失敗を確認しました。</summary>
    Failed,

    /// <summary>処理を開始していません。</summary>
    NotExecuted,

    /// <summary>実施有無または外部結果を確認できません。</summary>
    Unknown,

    /// <summary>取消による停止を確認しました。</summary>
    Cancelled,
}

