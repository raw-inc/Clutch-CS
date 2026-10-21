namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;

/// <summary>同じ候補集合に固定した一覧の読取り条件です。</summary>
public sealed record ConflictListQuery(int Limit = 50, string? Cursor = null);
