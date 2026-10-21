namespace ReArtWord.Contents.BackgroundService.Contracts.Terminal;

/// <summary>観測されたCoreのIPへ接続する一回限りの許可です。</summary>
/// <param name="Port">一接続専用のTCPポートです。</param>
/// <param name="Token">32バイトの秘密値のhexです。</param>
/// <param name="ExpiresAt">受付期限です。</param>
public sealed record TerminalTicket(int Port, string Token, DateTimeOffset ExpiresAt);
