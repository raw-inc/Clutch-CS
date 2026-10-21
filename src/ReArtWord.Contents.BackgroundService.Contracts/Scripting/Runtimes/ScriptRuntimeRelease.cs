namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

/// <summary>公式配布元から取得できる通常版ランタイムと、対象OS・アーキテクチャです。</summary>
public sealed record ScriptRuntimeRelease(string Language, string Version, string Platform, bool LongTermSupport, Uri Url, string? Sha256);
