using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Contracts.Targets;

/// <summary>負荷変動だけで待受を移動させないための、共有された担当PCです。</summary>
/// <param name="OperationId">担当を保存した操作。</param>
/// <param name="WorkflowId">待受を含むワークフロー。</param>
/// <param name="NodeId">待受ノード。</param>
/// <param name="ChoiceRevision">待受設定とPC選択条件の内容識別子。</param>
/// <param name="Targets">開始を許可するPCとPeer実体。</param>
/// <param name="Decision">選択時の候補とリソース観測。</param>
/// <param name="ConfirmedPeers">共有保存の確認対象として固定したPeer。</param>
public sealed record SourcePcAssignment(Guid OperationId, string WorkflowId, string NodeId, string ChoiceRevision,
    IReadOnlyList<PeerInfo> Targets, PcSelectionDecision Decision, IReadOnlyList<PeerInfo> ConfirmedPeers);
