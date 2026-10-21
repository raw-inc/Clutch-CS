using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>一つの実行先で確認できたノード結果です。</summary>
/// <param name="PeerId">実行先Peer。</param>
/// <param name="Result">確認できた結果。</param>
public sealed record TargetNodeResult(
    string PeerId,
    NodeResult Result);

