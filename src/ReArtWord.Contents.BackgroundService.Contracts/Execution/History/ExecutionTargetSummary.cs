namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.History;

/// <summary>実出力を含めず、固定対象ごとの確認結果を示します。</summary>
/// <param name="NodeId">対象ノード。</param>
/// <param name="PeerId">実行先Peer。</param>
/// <param name="Outcome">確認できた成否。</param>
/// <param name="ReasonCode">理由種類。</param>
/// <param name="Message">長さを制限した説明。</param>
public sealed record ExecutionTargetSummary(string NodeId, string PeerId, EOutcome Outcome, string? ReasonCode, string? Message);
