namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Safety;

/// <summary>操作内容とローカルで読み取った保存版です。</summary>
internal sealed record ManualSafetyEntry(ManualSafetySetting Setting, string Version, bool LocalOnly);
