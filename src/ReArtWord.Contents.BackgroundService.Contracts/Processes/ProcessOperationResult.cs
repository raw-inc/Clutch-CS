using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>プロセス操作の実行結果と観測状態です。</summary>
/// <param name="Outcome">副作用の確定範囲を含む結果です。</param>
/// <param name="Status">確認済みのアプリケーション状態です。</param>
/// <param name="Reason">安定した結果理由です。</param>
public sealed record ProcessOperationResult(EOutcome Outcome, ProcessStatus? Status, string? Reason = null);
