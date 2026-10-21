using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;

/// <summary>ペイロードを所有するPeerに保存する予約と開始実体です。</summary>
/// <param name="OwnerPeerId">自動交代しない起点Peerです。</param>
/// <param name="Plan">日時と更新入力です。</param>
/// <param name="PeerInstances">予約時に確認した対象実体です。</param>
/// <param name="State">開始前・開始後の状態です。</param>
/// <param name="Outcome">確認できた実施結果です。</param>
/// <param name="Update">元更新IDの最新確認結果です。</param>
/// <param name="Reason">保留・失敗・不明の固定診断です。</param>
public sealed record ScheduledUpdate(string OwnerPeerId, UpdateSchedulePlan Plan, IReadOnlyDictionary<string, Guid> PeerInstances,
    EUpdateScheduleState State, EOutcome Outcome, ClusterUpdateStatus? Update = null, string? Reason = null);
