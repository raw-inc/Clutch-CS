namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;

/// <summary>比較可能な本文、または取得できない理由です。</summary>
public sealed record ConflictDetail(StateConflict? Conflict, string? DetailReason = null);
