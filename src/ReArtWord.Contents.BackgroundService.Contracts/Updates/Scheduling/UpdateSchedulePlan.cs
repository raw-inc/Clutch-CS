using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;

/// <summary>予約時に確認した更新入力と、開始時刻・遅延許容範囲です。</summary>
/// <param name="StartAt">offset付きの絶対日時です。</param>
/// <param name="MaximumDelaySeconds">起点が停止・保留中の場合に開始を許す猶予秒数です。</param>
/// <param name="Update">予約全体を通じて同じOperationIdで追跡する更新要求です。</param>
public sealed record UpdateSchedulePlan(DateTimeOffset StartAt, int MaximumDelaySeconds, ClusterUpdateRequest Update);
