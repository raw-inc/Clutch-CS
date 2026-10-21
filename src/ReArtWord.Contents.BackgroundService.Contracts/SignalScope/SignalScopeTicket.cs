namespace ReArtWord.Contents.BackgroundService.Contracts.SignalScope;

/// <summary>キャプチャ接続一回だけに使用する短命な受付です。</summary>
/// <param name="Port">観測されたCoreの接続先ポートです。</param>
/// <param name="Token">32バイトの許可値を表すhexです。</param>
/// <param name="ExpiresAt">受付の期限です。</param>
public sealed record SignalScopeTicket(int Port, string Token, DateTimeOffset ExpiresAt);
