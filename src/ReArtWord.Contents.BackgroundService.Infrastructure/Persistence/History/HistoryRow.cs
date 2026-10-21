namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>圧縮前後で内容と既存チェックサムを維持する一行です。</summary>
/// <param name="Kind">保存行の種類です。</param>
/// <param name="Key">種類内で一意な識別子です。</param>
/// <param name="Scope">既存の用途索引です。</param>
/// <param name="Payload">元と同一のJSON本文です。</param>
/// <param name="Hash">元本文のSHA-256です。</param>
internal sealed record HistoryRow(string Kind, string Key, string? Scope, string Payload, string Hash);
