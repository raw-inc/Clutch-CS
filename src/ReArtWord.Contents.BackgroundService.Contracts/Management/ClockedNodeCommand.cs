using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>ブラウザーで一度だけ作成したノード要求と、ClientHostの時計で表した受付期限です。</summary>
public sealed record ClockedNodeCommand(RemoteExecutionRequest Request, RequestClockStamp Timing);
