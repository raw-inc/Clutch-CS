namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>件数とバイトを制限したスナップショットメタデータの一ページです。</summary>
/// <param name="Entries">状態本文を含まない候補。</param>
/// <param name="Cursor">次ページの位置。終端はnull。</param>
public sealed record SnapshotListPage(IReadOnlyList<SnapshotListSummary> Entries, string? Cursor);
