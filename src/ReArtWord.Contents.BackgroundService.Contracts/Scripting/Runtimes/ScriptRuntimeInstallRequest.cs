namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

/// <summary>一回のランタイム導入を固定します。配置先は受信側でログイン利用者から解決します。</summary>
public sealed record ScriptRuntimeInstallRequest(Guid OperationId, string Language, string Version);
