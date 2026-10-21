namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

/// <summary>検索で選んだパッケージの固定版候補を問い合わせます。</summary>
public sealed record ScriptPackageDetailsRequest(EScriptPackageKind Kind, string Id);
