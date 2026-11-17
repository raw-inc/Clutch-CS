using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Files;

/// <summary>ファイル転送の操作と換算済みの受付期限です。新形式で合意したTCP接続にだけ送信します。</summary>
public sealed record ClockedFileCommand(ManagementCommand Command, RequestClockStamp Timing);
