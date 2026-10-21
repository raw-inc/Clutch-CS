namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;

/// <summary>本文を含めず候補版と影響範囲を公開する競合一覧です。</summary>
public sealed record ConflictListSummary(string Id, string Scope, string Key, IReadOnlyList<string> CandidateVersions,
    IReadOnlyList<string> WorkflowIds, IReadOnlyList<string> OperationalFields, bool ValuesHidden, bool Resolvable,
    bool DetailAvailable, string? DetailReason = null);
