namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>一回の同期の期限を両端の起動後経過時間で固定します。再照会や逆方向の転送でも期限を作り直しません。</summary>
internal sealed record SyncTiming(Guid SourceInstanceId, long SourceUntilTicks, Guid TargetInstanceId, long TargetUntilTicks, long MaximumLifetimeTicks, long ClockUncertaintyTicks);
