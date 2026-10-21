namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;

/// <summary>人為的一時操作の直前に対象と影響を確認する方針です。</summary>
public enum ESafetyConfirmation
{
    /// <summary>追加確認なしで直ちに適用します。</summary>
    None,

    /// <summary>自動停止を一時的に続行する場合だけ確認します。</summary>
    Continue,

    /// <summary>一時停止と一時続行のどちらにも確認を求めます。</summary>
    Always,
}
