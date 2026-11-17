namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>最初の配送時に確認した、元の対象実体の時計による受付期限です。</summary>
public sealed record RequestClockStamp(Guid InstanceId, long StartByTicks, DateTimeOffset SourceNow);
