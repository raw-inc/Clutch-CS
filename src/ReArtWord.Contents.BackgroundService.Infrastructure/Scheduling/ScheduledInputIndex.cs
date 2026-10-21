using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;

/// <summary>未完了の予定入力だけを、終了済み入力の本文を読み直さず列挙するための索引です。</summary>
/// <param name="ChangeId">この索引を確定した操作。</param>
/// <param name="ConfirmedPeers">共有時に固定した確認対象。</param>
/// <param name="Entries">未完了の入力と保持量。</param>
internal sealed record ScheduledInputIndex(Guid ChangeId, IReadOnlyList<PeerInfo> ConfirmedPeers, IReadOnlyList<ScheduledInputIndexEntry> Entries);
