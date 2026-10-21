using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>待受自体の寿命と一件処理を分けて表示する情報です。</summary>
/// <param name="Id">待受の開始要求。</param>
/// <param name="WorkflowId">ワークフロー。</param>
/// <param name="NodeId">入力ノード。</param>
/// <param name="PeerId">待受の実Peer。</param>
/// <param name="Revision">現在の取得開始に用いる設定版。</param>
/// <param name="StartedAt">開始時刻。</param>
/// <param name="Status">待受の状態。</param>
/// <param name="Result">待受終了時の結果。</param>
/// <param name="PeerInstanceId">待受を開始したPeerの起動ID。旧記録では未設定。</param>
public sealed record SourceExecutionInfo(Guid Id, string WorkflowId, string NodeId, string PeerId, string Revision, DateTimeOffset StartedAt, NodeSourceStatus Status, NodeResult? Result = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? PeerInstanceId = null);
