namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>待機を始めたOS起動回・稼働時間と、そのときの表示日時です。</summary>
internal readonly record struct ProcessWaitOrigin(string BootId, TimeSpan Uptime, DateTimeOffset Wall);
