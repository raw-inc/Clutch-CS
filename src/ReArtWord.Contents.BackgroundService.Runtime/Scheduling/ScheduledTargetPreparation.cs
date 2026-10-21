using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

/// <summary>未開始の予定を実際に開始できる要求へ変換した結果です。</summary>
/// <param name="Request">今回開始する実体と受付期限を固定した要求。</param>
/// <param name="Rejection">開始しなかった場合の理由。</param>
internal sealed record ScheduledTargetPreparation(RemoteExecutionRequest Request, NodeResult? Rejection = null);
