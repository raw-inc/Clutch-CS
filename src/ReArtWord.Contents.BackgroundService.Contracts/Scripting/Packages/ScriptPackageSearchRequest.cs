namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

/// <summary>公開配布元への有限件の検索です。導入操作は含みません。</summary>
public sealed record ScriptPackageSearchRequest(EScriptPackageKind Kind, string Query, int Offset = 0);
