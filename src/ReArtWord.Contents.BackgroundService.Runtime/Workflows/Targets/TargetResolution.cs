using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

/// <summary>開始時に固定した実行先と解決できなかった理由です。</summary>
/// <param name="Scope">指定された実行範囲。</param>
/// <param name="Peers">開始時の実体を含む対象集合。</param>
/// <param name="Error">対象集合を成立させられない場合の理由。</param>
/// <param name="Selection">PC選択に使用した候補と観測情報。</param>
public sealed record TargetResolution(EExecutionScope Scope, IReadOnlyList<PeerInfo> Peers, NodeResult? Error = null, PcSelectionDecision? Selection = null);
