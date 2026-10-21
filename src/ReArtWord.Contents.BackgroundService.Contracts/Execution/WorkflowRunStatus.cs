using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;

namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>手動実行またはSourceから発生した一件の実行状況です。</summary>
/// <param name="OperationId">要求識別子。</param>
/// <param name="PeerId">実行全体を追跡するPeer。</param>
/// <param name="WorkflowId">定義識別子。</param>
/// <param name="Revision">開始時に固定した版。</param>
/// <param name="Outcome">未完了や実行Peer喪失ではUnknownです。</param>
/// <param name="Running">このPeerで実タスクを追跡しているか。</param>
/// <param name="Report">確認した一件の実行全体の結果。</param>
/// <param name="Reason">未実行・不明の説明種類。</param>
/// <param name="Summary">詳細省略時にも保存する確定結果。</param>
/// <param name="DetailReason">詳細を省略した理由。</param>
/// <param name="DetailsUnavailable">このPeerのキャッシュに完全結果がなく、保持した要約だけを読めるか。</param>
/// <param name="OwnerInstanceId">元の実行を追跡するPeer実体。古い記録で未確認の場合はnull。</param>
public sealed record WorkflowRunStatus(Guid OperationId, string PeerId, string WorkflowId, string? Revision, EOutcome Outcome, bool Running, ExecutionReport? Report = null, string? Reason = null,
    ExecutionReportSummary? Summary = null, string? DetailReason = null, bool DetailsUnavailable = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OwnerInstanceId = null);
