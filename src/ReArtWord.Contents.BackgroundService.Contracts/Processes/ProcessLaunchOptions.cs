namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>アカウント・セッションと初期表示を独立して選ぶ起動設定です。</summary>
public sealed record ProcessLaunchOptions(EProcessLaunchSession Session = EProcessLaunchSession.Automatic, EProcessWindowMode Window = EProcessWindowMode.Default);
