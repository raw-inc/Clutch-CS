using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>アーカイブ操作の受付と確定結果を区別します。</summary>
/// <param name="OperationId">元の操作IDです。</param>
/// <param name="Stage">実行段階です。</param>
/// <param name="Completed">処理が終了したかです。成功とは別です。</param>
/// <param name="Outcome">確認できた結果です。</param>
/// <param name="Reason">未完了・失敗の理由です。</param>
/// <param name="File">書出し完了時の転送情報です。</param>
/// <param name="Steps">取り込みの段階別結果です。</param>
public sealed record ArchiveOperationResult(Guid OperationId, string Stage, bool Completed, EOutcome Outcome,
    string? Reason = null, ArchiveTransfer? File = null, IReadOnlyList<ArchiveStepResult>? Steps = null);
