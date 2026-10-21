namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>JSON形式でもSQLiteと同じ基準点本文を使用します。</summary>
/// <param name="Info">確定位置と容量です。</param>
/// <param name="Payload">非圧縮またはzstdの本文です。</param>
/// <param name="Hash">非圧縮本文の検査値です。</param>
/// <param name="DeleteAt">短縮を遡及させない削除期限です。</param>
internal sealed record JsonHistoryCheckpoint(Contracts.Persistence.History.HistoryCheckpointInfo Info, byte[] Payload, string Hash, DateTimeOffset? DeleteAt);
