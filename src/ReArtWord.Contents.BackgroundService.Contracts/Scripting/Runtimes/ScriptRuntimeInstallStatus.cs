namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

/// <summary>再送せずに表示するPeerの導入状態と直近の操作です。</summary>
public sealed record ScriptRuntimeInstallStatus(bool Busy, IReadOnlyList<ScriptRuntimeInstallOperation> Operations);
