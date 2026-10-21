namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

/// <summary>公開配布元から取得した検索候補です。未取得の版や件数はnullです。</summary>
public sealed record ScriptPackageSearchItem(string Id, string? Version, string Description, long? Downloads = null);
