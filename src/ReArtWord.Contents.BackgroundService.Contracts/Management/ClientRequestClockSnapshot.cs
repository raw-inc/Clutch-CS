namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>ブラウザーが操作の受付期限を固定するためのClientHostの時計です。</summary>
/// <param name="InstanceId">この時計を所有するClientHost実体です。</param>
/// <param name="ElapsedTicks">時計の起点から経過したTimeSpanのtick数です。</param>
/// <param name="ObservedAt">要求本文の日付表記に使う、この標本の取得日時です。</param>
public sealed record ClientRequestClockSnapshot(Guid InstanceId, long ElapsedTicks, DateTimeOffset ObservedAt);
