namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

/// <summary>検索の空振りと配布元の取得失敗を区別します。</summary>
public sealed record ScriptPackageSearchResult(IReadOnlyList<ScriptPackageSearchItem> Items, bool HasMore, string? Notice = null, string? Problem = null);
