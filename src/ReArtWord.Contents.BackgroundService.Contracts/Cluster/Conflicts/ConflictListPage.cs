namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;

/// <summary>取得した有限範囲と次の候補位置です。</summary>
public sealed record ConflictListPage(IReadOnlyList<ConflictListSummary> Entries, string? Cursor);
