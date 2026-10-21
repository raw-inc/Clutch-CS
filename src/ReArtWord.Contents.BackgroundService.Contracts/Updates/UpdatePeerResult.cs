using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates;

/// <summary>一Peerの更新進行と実確認結果です。</summary>
/// <param name="OperationId">一回の更新識別子です。</param>
/// <param name="PeerId">対象Peerです。</param>
/// <param name="Phase">確認済みの進行段階です。</param>
/// <param name="Outcome">進行中を成功へ丸めない結果です。</param>
/// <param name="ReleaseId">実適用を確認したリリースです。</param>
/// <param name="BackupPath">退避を確認した旧リリースのパスです。</param>
/// <param name="InstanceId">稼働確認で確認した新しい常駐実体です。</param>
/// <param name="Reason">安定した結果理由です。</param>
/// <param name="PayloadPath">準備時に許可したPeerローカルの転送先です。</param>
/// <param name="PreparedInstanceId">副作用開始を許可した旧実体です。</param>
public sealed record UpdatePeerResult(Guid OperationId, string PeerId, EUpdatePhase Phase, EOutcome Outcome, string? ReleaseId = null, string? BackupPath = null, Guid? InstanceId = null, string? Reason = null, string? PayloadPath = null, Guid? PreparedInstanceId = null);
