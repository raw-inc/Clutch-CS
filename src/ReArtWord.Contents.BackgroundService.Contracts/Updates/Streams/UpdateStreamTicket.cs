namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;

/// <summary>固定した更新ジョブと送信元に対して一度だけ使える転送許可です。</summary>
/// <param name="Token">推測不能な一回の接続トークンです。</param>
/// <param name="ExpiresAt">接続開始を受け入れる期限です。</param>
public sealed record UpdateStreamTicket(Guid Token, DateTimeOffset ExpiresAt);
