namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;

/// <summary>編集CASとは区別した、永続状態の未解消分岐です。</summary>
/// <param name="Id">対象スコープとキーの識別子です。</param>
/// <param name="Scope">保存用途です。</param>
/// <param name="Key">用途内の対象です。</param>
/// <param name="Candidates">対等に保持する候補です。</param>
/// <param name="WorkflowIds">新規利用を隔離するワークフローです。</param>
/// <param name="OperationalFields">影響する共通設定の項目です。</param>
/// <param name="ValuesHidden">秘密値等を公開していないか。</param>
/// <param name="Resolvable">既存の型検証で明示解消できるか。</param>
public sealed record StateConflict(string Id, string Scope, string Key, IReadOnlyList<StateConflictCandidate> Candidates,
    IReadOnlyList<string> WorkflowIds, IReadOnlyList<string> OperationalFields, bool ValuesHidden, bool Resolvable);
