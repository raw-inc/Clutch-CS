using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>HTTPから独立した管理操作の実行結果です。</summary>
/// <param name="Outcome">確認できた結果。</param>
/// <param name="Value">ドメインサービスが返した内容。</param>
/// <param name="Reason">非成功理由。</param>
public sealed record ManagementReply(EOutcome Outcome, NodeValue? Value = null, string? Reason = null);
