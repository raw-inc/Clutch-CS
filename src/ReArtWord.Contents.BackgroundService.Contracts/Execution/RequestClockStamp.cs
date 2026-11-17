namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>送信先Coreの起動後の経過時間で表した受付期限です。再送時もこの値を使い、期限を延ばしません。</summary>
/// <param name="InstanceId">時計を照会したCoreプロセスの実体IDです。再起動後の別プロセスには使えません。</param>
/// <param name="StartByTicks">Core側の受付時計の起点から期限までの時間を、TimeSpanのtick単位で表した値です。</param>
/// <param name="SourceNow">要求元の時刻系で表した換算時の基準日時です。</param>
public sealed record RequestClockStamp(Guid InstanceId, long StartByTicks, DateTimeOffset SourceNow);
