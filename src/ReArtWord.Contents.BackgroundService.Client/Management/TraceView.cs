using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

namespace ReArtWord.Contents.BackgroundService.Client.Management;

/// <summary>一時追跡、保存通常方針、停止保留を別々に表示します。</summary>
internal sealed record TraceView(bool Loaded, IReadOnlyList<TraceStatus> States, TraceStopState Closing, IReadOnlyList<TraceStopTarget> Targets, string? Version = null);
