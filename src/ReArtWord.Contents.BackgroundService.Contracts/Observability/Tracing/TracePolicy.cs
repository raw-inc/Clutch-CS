namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

/// <summary>その一件へ適用する詳細追跡方針です。</summary>
/// <param name="Enabled">詳細追跡を行うか。</param>
/// <param name="Selection">未指定時は入力・出力を含みます。</param>
public sealed record TracePolicy(bool Enabled, TraceSelection? Selection = null);
