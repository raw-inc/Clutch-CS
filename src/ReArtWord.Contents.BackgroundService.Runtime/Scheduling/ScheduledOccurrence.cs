using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

/// <summary>発生時点で固定した時間指定と、未開始対象の待機方針です。</summary>
/// <param name="TimelineKey">同じ時間指定と実行区分の識別子。</param>
/// <param name="SourceNodeId">発生元の時間指定ノード。</param>
/// <param name="SourceId">元の待受の開始ID。</param>
/// <param name="ScheduledAt">本来の予定時刻。</param>
/// <param name="MissedPolicy">予定時刻に開始できない回の扱い。</param>
/// <param name="Tolerance">許容する遅延時間。</param>
public sealed record ScheduledOccurrence(string TimelineKey, string SourceNodeId, Guid SourceId, DateTimeOffset ScheduledAt,
    EScheduleMissedPolicy MissedPolicy, TimeSpan Tolerance);
