namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

/// <summary>通信の生内容を確認する際の範囲情報です。</summary>
/// <param name="Protocol">UDP/TCP/HTTP等の通信種別。</param>
/// <param name="Outgoing">送信側の記録か。</param>
/// <param name="OriginalLength">伏字・切詰め前の実バイト数。</param>
/// <param name="Truncated">保存範囲を先頭の一部に限定したか。</param>
public sealed record TraceRawInfo(string Protocol, bool Outgoing, int OriginalLength, bool Truncated);
