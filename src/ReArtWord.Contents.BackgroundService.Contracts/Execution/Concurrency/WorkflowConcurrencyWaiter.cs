namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;

/// <summary>入力値のコピーを持たない、一件の待機表示です。</summary>
/// <param name="TicketId">この到達の識別子。</param>
/// <param name="RunId">元の実行。</param>
/// <param name="OwnerPeerId">元の実行を所有するPeer。</param>
/// <param name="QueuedAt">この担当で受付を開始した時刻。</param>
/// <param name="Deadline">待機期限。無期限ならnull。</param>
/// <param name="ProceedOnTimeout">期限後に上限を超えて進行する設定か。</param>
public sealed record WorkflowConcurrencyWaiter(Guid TicketId, Guid RunId, string OwnerPeerId, DateTimeOffset QueuedAt,
    DateTimeOffset? Deadline, bool ProceedOnTimeout);
