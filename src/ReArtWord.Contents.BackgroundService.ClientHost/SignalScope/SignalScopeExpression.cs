namespace ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;

/// <summary>ブラウザーで解釈した式を、値と深さを再検証して扱います。</summary>
internal sealed record SignalScopeExpression(string? Term = null, string? Op = null, SignalScopeExpression? Left = null, SignalScopeExpression? Right = null);
