namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>後継プロセスの照合条件と、終了確定前の確認待ち時間です。</summary>
/// <param name="TimeoutMilliseconds">終了確定前の確認待ち時間。0はこの待機を省略します。</param>
/// <param name="Match">終了確認中と復旧待機中に使用する後継候補の照合条件です。</param>
public sealed record ProcessRestartDetection(int TimeoutMilliseconds = 0, EProcessRestartMatch Match = EProcessRestartMatch.ExecutableAndArguments);
