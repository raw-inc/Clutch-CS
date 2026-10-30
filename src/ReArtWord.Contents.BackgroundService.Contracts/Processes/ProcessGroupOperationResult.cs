using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>操作要求の受付結果と、その後に照会できるグループ状態です。</summary>
/// <param name="Outcome">要求を受け付けたかを表します。</param>
/// <param name="Status">受付後の状態です。操作の完了はこの状態の結果で確認します。</param>
/// <param name="Reason">受け付けなかった場合の理由です。</param>
public sealed record ProcessGroupOperationResult(EOutcome Outcome, ProcessGroupStatus? Status = null, string? Reason = null);
