namespace ReArtWord.Contents.BackgroundService.Contracts.Observability;

/// <summary>同じ事象を処理済みの購読ノードを、Peer間でも識別します。</summary>
/// <param name="WorkflowId">購読ノードを持つワークフロー。</param>
/// <param name="NodeId">購読ノード。</param>
public sealed record WorkflowHookStep(string WorkflowId, string NodeId);
