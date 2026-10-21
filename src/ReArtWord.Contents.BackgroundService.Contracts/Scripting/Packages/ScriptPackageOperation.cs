using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

/// <summary>受付を完了と混同せず、再起動後も元操作の結果を照会する記録です。</summary>
public sealed record ScriptPackageOperation(ScriptPackageChangeRequest Request, string Fingerprint, string Phase, EOutcome Outcome,
    bool Completed, DateTimeOffset UpdatedAt, string? Revision = null, string? Reason = null);
