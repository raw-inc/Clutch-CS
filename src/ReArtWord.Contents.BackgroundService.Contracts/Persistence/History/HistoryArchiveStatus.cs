namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;

/// <summary>圧縮対象の進捗と、SQLiteの実ファイル容量・再利用可能領域を区別して表示します。</summary>
/// <param name="Supported">SQLiteの履歴保守を利用できるかです。</param>
/// <param name="HotRecords">通常形式の履歴候補数です。保護対象を含みます。</param>
/// <param name="ArchivedRecords">圧縮索引に移った件数です。</param>
/// <param name="Blocks">保持しているzstdブロック数です。</param>
/// <param name="CompressedBytes">圧縮本文の容量です。</param>
/// <param name="ExpandedBytes">圧縮前のブロック本文の容量です。</param>
/// <param name="DatabaseBytes">SQLiteの実ファイル容量です。</param>
/// <param name="ReusableBytes">SQLiteが次の書込みへ再利用できる領域です。</param>
/// <param name="Problem">直近の保守で確認できなかった理由です。</param>
/// <param name="UnindexedRows">初回の分類確認がまだ到達していない通常行数です。</param>
/// <param name="ProtectedHotRecords">未確定・容量等の理由で通常形式へ残す確認済み件数です。</param>
/// <param name="LegacyRecords">旧形式と確認済みで、移行前の通常行に残る件数です。保護対象を含み、未対応媒体ではnullです。</param>
/// <param name="UnreviewedRecords">形式・安全性の背景検証が未完了の候補数です。未対応媒体ではnullです。</param>
public sealed record HistoryArchiveStatus(bool Supported, long HotRecords, long ArchivedRecords, long Blocks,
    long CompressedBytes, long ExpandedBytes, long DatabaseBytes, long ReusableBytes, string? Problem = null, long UnindexedRows = 0, long ProtectedHotRecords = 0,
    long? LegacyRecords = null, long? UnreviewedRecords = null);
