namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;

/// <summary>実行先が変わっても同じ区間の取得を識別する、値本文を含まない受付です。</summary>
/// <param name="TicketId">この区間への一回の到達を識別する番号。</param>
/// <param name="WorkflowId">実行ワークフロー。</param>
/// <param name="NodeId">制限開始ノード。</param>
/// <param name="RunId">受付を所有する実行。</param>
/// <param name="OwnerPeerId">実行を所有するPeer。</param>
/// <param name="OwnerInstanceId">所有Peerの起動実体。</param>
/// <param name="MaximumConcurrency">開始時に固定した同時実行数。</param>
/// <param name="WaitTimeoutMilliseconds">待機期限。0は無期限。</param>
/// <param name="ProceedOnTimeout">待機期限後、占有数へ加算して上限を超えて開始するか。</param>
public sealed record WorkflowConcurrencyRequest(Guid TicketId, string WorkflowId, string NodeId, Guid RunId,
    string OwnerPeerId, Guid OwnerInstanceId, int MaximumConcurrency, int WaitTimeoutMilliseconds, bool ProceedOnTimeout);
