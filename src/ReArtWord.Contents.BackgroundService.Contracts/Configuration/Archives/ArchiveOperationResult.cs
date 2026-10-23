using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>アーカイブ操作の進捗と結果。</summary>
/// <param name="OperationId">操作ID。</param>
/// <param name="Stage">実行段階です。</param>
/// <param name="Completed">処理が終了した場合はtrue。成否はOutcomeで確認します。</param>
/// <param name="Outcome">確認できた結果です。</param>
/// <param name="Reason">状態の詳細やエラーの理由。</param>
/// <param name="File">書き出し済みファイルの転送情報。</param>
/// <param name="Steps">取り込みの各工程の結果。</param>
public sealed record ArchiveOperationResult(Guid OperationId, string Stage, bool Completed, EOutcome Outcome,
    string? Reason = null, ArchiveTransfer? File = null, IReadOnlyList<ArchiveStepResult>? Steps = null);
