namespace ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;

/// <summary>引継ぎ要否を判断するための、本文を含まない予定入力の情報です。</summary>
/// <param name="Id">元の一件の実行ID。</param>
/// <param name="WorkflowId">元のワークフロー。</param>
/// <param name="Bytes">保持する入力のJSONサイズ。</param>
/// <param name="OwnerPeerId">現在の管理Peer。</param>
/// <param name="OwnerInstanceId">管理Peerの起動ID。</param>
/// <param name="DispatchPermitted">配送の開始許可を確定したか。</param>
internal sealed record ScheduledInputIndexEntry(Guid Id, string WorkflowId, long Bytes, string OwnerPeerId, Guid OwnerInstanceId, bool DispatchPermitted);
