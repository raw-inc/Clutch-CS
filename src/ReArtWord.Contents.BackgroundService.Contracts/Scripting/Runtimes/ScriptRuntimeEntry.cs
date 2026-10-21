namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

/// <summary>一つの実行ファイルの検出元、実版、登録状態と取得不能を示します。</summary>
public sealed record ScriptRuntimeEntry(string Path, string? Version, string Source, bool Registered, bool Available, string? Problem = null);
