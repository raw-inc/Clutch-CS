using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Client.Management;

/// <summary>登録版とプロセスの実観測を分離します。</summary>
internal sealed record ProcessView(string Version, ProcessDefinition Definition, ProcessOperationResult Observed, IReadOnlyList<ProcessScheduleStatus>? Schedules = null);
