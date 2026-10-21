namespace ReArtWord.Contents.BackgroundService.Contracts.Desktop;

/// <summary>管理通信で確定した一回限りの映像接続です。</summary>
/// <param name="Port">対象CoreのTCP待受ポート。</param>
/// <param name="Token">接続を一度だけ許可する32バイトのhex値。</param>
/// <param name="ExpiresAt">新規接続の期限。</param>
/// <param name="CodecDetails">Coreが追加コーデック設定を受信できるかです。</param>
public sealed record DesktopTicket(int Port, string Token, DateTimeOffset ExpiresAt, bool CodecDetails = false);
