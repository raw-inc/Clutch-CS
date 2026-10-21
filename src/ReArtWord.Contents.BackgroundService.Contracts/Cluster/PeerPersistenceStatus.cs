namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster;

/// <summary>候補本文や対象名を広報せず、他の接続先からも保存問題を発見できます。</summary>
/// <param name="UnsavedActive">永続保存できていない現在有効な項目数です。</param>
/// <param name="UnsavedCandidates">未採用を含む永続保存できていない候補数です。</param>
/// <param name="LocalUnconfirmed">共有へ統合していないローカル項目数です。</param>
public sealed record PeerPersistenceStatus(int UnsavedActive, int UnsavedCandidates, int LocalUnconfirmed);
