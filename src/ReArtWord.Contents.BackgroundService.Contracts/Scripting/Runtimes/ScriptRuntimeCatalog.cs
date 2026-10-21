namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

/// <summary>実行Peerの候補一覧と、明示した既定値・自動選定先を分けます。</summary>
public sealed record ScriptRuntimeCatalog(string Language, string? Revision, string? DefaultPath, string? EffectivePath,
    IReadOnlyList<ScriptRuntimeEntry> Entries, string? InstallDirectory = null, string? InstallProblem = null);
