namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster;

/// <summary>Peerの参加状態です。</summary>
public enum EPeerStatus
{
    /// <summary>初期参加の準備中です。</summary>
    Joining,

    /// <summary>保存・共有状態を同期中です。</summary>
    Syncing,

    /// <summary>通常参加に必要な条件が揃っています。</summary>
    Ready,

    /// <summary>通信または参加条件を確認できません。</summary>
    Unavailable,

    /// <summary>人が明示離脱させています。</summary>
    Detached,

    /// <summary>識別または状態の競合解消が必要です。</summary>
    Conflict,

    /// <summary>通常処理を始めず復旧・診断を提供しています。</summary>
    RecoveryOnly,

    /// <summary>内容競合の依存範囲を除き、検証済み状態で協調できます。</summary>
    Degraded,
}
