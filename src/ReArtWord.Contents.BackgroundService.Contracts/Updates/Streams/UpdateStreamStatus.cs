namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;

/// <summary>応答消失時もデータを再送せず照会できる短命な転送結果です。</summary>
/// <param name="Token">元の接続トークンです。</param>
/// <param name="Bytes">この接続で受信したバイト数です。</param>
/// <param name="Completed">終了が確認できたかです。</param>
/// <param name="Reason">成功ならnull、それ以外は一般的な失敗理由です。</param>
public sealed record UpdateStreamStatus(Guid Token, long Bytes, bool Completed, string? Reason = null);
