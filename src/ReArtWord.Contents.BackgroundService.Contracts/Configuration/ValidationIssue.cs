namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration;

/// <summary>設定・参照を成立させるために修正が必要な問題です。</summary>
/// <param name="Code">安定した理由種類。</param>
/// <param name="Message">人向けの説明。</param>
/// <param name="NodeId">問題のあるノード。</param>
/// <param name="Path">設定項目または接続。</param>
public sealed record ValidationIssue(
    string Code,
    string Message,
    string? NodeId = null,
    string? Path = null);

