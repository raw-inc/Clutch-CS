namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>待受の正常停止または緊急取消です。</summary>
/// <param name="WorkflowId">ワークフロー。</param>
/// <param name="NodeId">待受。</param>
/// <param name="Cancel">進行中入力も取り消すか。</param>
/// <param name="SourceId">停止する待受の実行ID。</param>
/// <param name="PeerInstanceId">待受を所有するPeerのプロセス実体。</param>
public sealed record SourceStopRequest(string WorkflowId, string NodeId, bool Cancel = false, Guid? SourceId = null, Guid? PeerInstanceId = null);
