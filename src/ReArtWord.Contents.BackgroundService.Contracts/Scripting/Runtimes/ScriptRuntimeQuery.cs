namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

/// <summary>候補を必要時だけ再検出し、通常の一覧取得では確認結果を再利用します。</summary>
public sealed record ScriptRuntimeQuery(string Language, bool Refresh = false);
