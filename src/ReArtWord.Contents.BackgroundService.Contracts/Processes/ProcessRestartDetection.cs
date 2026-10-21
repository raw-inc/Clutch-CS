namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>終了を確定する前に、自律再起動による後継プロセスを確認する設定です。</summary>
/// <param name="TimeoutMilliseconds">確認待ち時間。0は無効です。</param>
/// <param name="Match">後継候補の照合条件です。</param>
public sealed record ProcessRestartDetection(int TimeoutMilliseconds = 0, EProcessRestartMatch Match = EProcessRestartMatch.ExecutableAndArguments);
