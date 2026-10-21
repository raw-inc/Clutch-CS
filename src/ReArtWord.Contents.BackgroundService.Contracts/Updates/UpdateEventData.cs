using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates;

/// <summary>更新の確認済み事象を、配布物パスや接続情報を含めずHookへ渡します。</summary>
/// <param name="OperationId">元の更新操作。</param>
/// <param name="Outcome">確認した成否。</param>
/// <param name="Completed">全体操作の終端を確認したか。</param>
/// <param name="PeerId">対象別事象のPeer。全体事象ではnull。</param>
/// <param name="Phase">対象別に確認した段階。</param>
/// <param name="PreparedInstanceId">更新を許可した旧実体。</param>
/// <param name="InstanceId">確認した更新後実体。</param>
/// <param name="ReleaseId">確認したリリース。</param>
/// <param name="Reason">安定した結果理由。</param>
/// <param name="AttemptOperationId">全体更新から派生した一試行のID。</param>
/// <param name="ClusterId">複数クラスタ更新での対象クラスタ。</param>
/// <param name="Stage">全体更新・ClientHost更新の確認済み段階。</param>
public sealed record UpdateEventData(Guid OperationId, EOutcome Outcome, bool Completed, string? PeerId = null, EUpdatePhase? Phase = null,
    Guid? PreparedInstanceId = null, Guid? InstanceId = null, string? ReleaseId = null, string? Reason = null,
    Guid? AttemptOperationId = null, string? ClusterId = null, string? Stage = null);
