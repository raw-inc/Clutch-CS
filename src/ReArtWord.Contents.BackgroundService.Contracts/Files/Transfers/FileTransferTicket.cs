namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>固定Core実体が発行する一回限りのファイル通信許可です。</summary>
/// <param name="Port">Coreの明示IP上のTCPポートです。</param>
/// <param name="Token">32バイトの秘密のhex値です。</param>
/// <param name="ExpiresAt">新規接続を受理する期限です。</param>
public sealed record FileTransferTicket(int Port, string Token, DateTimeOffset ExpiresAt);
