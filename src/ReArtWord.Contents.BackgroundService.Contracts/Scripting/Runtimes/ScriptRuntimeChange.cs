namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

/// <summary>登録済みパスを追加し、明示指定時だけ既定値を変更します。空パスの既定化は自動選定へ戻します。</summary>
public sealed record ScriptRuntimeChange(string Language, string? Path, bool SetDefault = false, string? ExpectedRevision = null);
