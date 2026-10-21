namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;

/// <summary>ローカル変更の同じ確認集合に固定した一覧条件です。</summary>
public sealed record DetachedWorkflowListQuery(int Limit = 50, string? Cursor = null);
