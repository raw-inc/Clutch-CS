namespace ReArtWord.Contents.BackgroundService.Contracts.Collaboration;

/// <summary>送信元Coreの受付順と有効期間を持つ、一度だけ配送する参加通知です。</summary>
/// <param name="Presence">表示するUI実体と編集位置です。</param>
/// <param name="Sequence">同じ送信元Core実体で単調に増える受付連番です。</param>
/// <param name="LifetimeTicks">新しい通知を受信してから表示する期間です。</param>
public sealed record PresenceAnnouncement(PresenceUpdate Presence, long Sequence, long LifetimeTicks);
