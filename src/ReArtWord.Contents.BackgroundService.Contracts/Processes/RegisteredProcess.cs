namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>競合を検出して編集できるアプリケーション登録内容です。</summary>
/// <param name="Version">確認した登録版です。</param>
/// <param name="Definition">登録済みの操作範囲です。</param>
public sealed record RegisteredProcess(string Version, ProcessDefinition Definition);
