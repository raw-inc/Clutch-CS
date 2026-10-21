namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;

/// <summary>確定イベント列の再開位置と復旧基準点の保存状態です。</summary>
/// <param name="Id">基準点の識別子です。</param>
/// <param name="Sequence">この保存先で包含した最後の確定イベント位置です。</param>
/// <param name="CreatedAt">同じ読取り版を取得した日時です。</param>
/// <param name="Compressed">本文をzstdで保持しているかです。</param>
/// <param name="StoredBytes">実際に保持する本文の容量です。</param>
/// <param name="RawBytes">展開後の本文容量です。</param>
public sealed record HistoryCheckpointInfo(string Id, long Sequence, DateTimeOffset CreatedAt, bool Compressed, long StoredBytes, long RawBytes);
