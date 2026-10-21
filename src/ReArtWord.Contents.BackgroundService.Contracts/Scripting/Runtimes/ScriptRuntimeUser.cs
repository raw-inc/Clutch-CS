namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

/// <summary>要求受付時に確認したログイン利用者と、そのホーム・検索用PATHです。</summary>
public sealed record ScriptRuntimeUser(string Id, string Name, string HomeDirectory, string? SearchPath = null);
