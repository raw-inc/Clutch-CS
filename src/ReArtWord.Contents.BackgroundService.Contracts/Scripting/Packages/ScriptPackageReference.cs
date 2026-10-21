namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

/// <summary>導入対象または復元済みの、明示した名前と版です。</summary>
public sealed record ScriptPackageReference(string Id, string Version);
