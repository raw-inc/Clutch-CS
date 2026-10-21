namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>実行の成否と確認できた適用段階を保持します。</summary>
/// <param name="OperationId">元の操作ID。</param>
/// <param name="Outcome">成功・失敗・未実行・不明の区別。</param>
/// <param name="Reason">表示可能な理由コード。</param>
/// <param name="Revision">再読取りできた変更後の内容版。</param>
/// <param name="CompletedSteps">完了を確認した段階。</param>
public sealed record DeviceChangeResult(Guid OperationId, Execution.EOutcome Outcome, string? Reason = null, string? Revision = null, IReadOnlyList<string>? CompletedSteps = null);
