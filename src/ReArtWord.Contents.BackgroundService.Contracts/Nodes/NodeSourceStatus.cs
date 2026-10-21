namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>継続待受の利用可能性とその理由です。</summary>
/// <param name="State">待受状態。</param>
/// <param name="ReasonCode">安定した理由種類。</param>
/// <param name="Message">人向け説明。</param>
public sealed record NodeSourceStatus(ESourceState State, string? ReasonCode = null, string? Message = null);
