using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>確認できた管理処理結果です。</summary>
/// <param name="Outcome">処理結果。</param>
/// <param name="Value">正常出力。</param>
/// <param name="Reason">非成功理由。</param>
public sealed record ClusterResponse(EOutcome Outcome, NodeValue? Value = null, string? Reason = null);
