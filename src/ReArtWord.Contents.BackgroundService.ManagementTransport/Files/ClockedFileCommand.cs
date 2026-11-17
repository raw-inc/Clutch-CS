using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Files;

/// <summary>対応を確認したファイル接続にだけ、元の操作と対象実体の受付期限を送ります。</summary>
public sealed record ClockedFileCommand(ManagementCommand Command, RequestClockStamp Timing);
