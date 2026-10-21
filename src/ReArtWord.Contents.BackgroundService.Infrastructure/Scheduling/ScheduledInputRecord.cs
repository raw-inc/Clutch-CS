using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;

/// <summary>予定入力の保持と、配送開始の可能性が生じた境界を区別して保存します。</summary>
/// <param name="Id">元の一件の実行ID。</param>
/// <param name="WorkflowId">元のワークフロー。</param>
/// <param name="Revision">取得時の設定版。</param>
/// <param name="Source">入力を取得した待受。</param>
/// <param name="OwnerPeerId">現在この一件を管理するPeer。</param>
/// <param name="OwnerInstanceId">管理するPeerの起動ID。</param>
/// <param name="DispatchPermitted">一件の配送開始を許可済みか。trueを未開始へ戻しません。</param>
/// <param name="ChangeId">この記録を確定した操作。</param>
/// <param name="ConfirmedPeers">共有時に固定した確認対象。</param>
/// <param name="Input">未完了の入力。終了後は解放します。</param>
/// <param name="Result">確認した一件の結果。</param>
/// <param name="Outcome">終了・拒否・結果不明を確認した場合の結果。nullは未完了。</param>
/// <param name="Reason">保持・引継ぎ・確認不能の理由。</param>
/// <param name="CancellationRequested">担当交代後にも維持する、元の一件への取消指示。</param>
internal sealed record ScheduledInputRecord(Guid Id, string WorkflowId, string Revision, SourceExecutionInfo Source, string OwnerPeerId, Guid OwnerInstanceId,
    bool DispatchPermitted, Guid ChangeId, IReadOnlyList<PeerInfo> ConfirmedPeers, ScheduleInputSnapshot? Input, ExecutionResultView? Result = null, EOutcome? Outcome = null, string? Reason = null,
    bool CancellationRequested = false);
