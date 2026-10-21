namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>JSON保存先の一件の履歴を、元本文と同じ原子的置換に含めます。</summary>
/// <param name="Kind">元の保存行種別です。</param>
/// <param name="Key">元の保存キーです。</param>
/// <param name="Scope">用途です。</param>
/// <param name="Data">zstd本文です。</param>
/// <param name="Length">展開上限です。</param>
/// <param name="Hash">圧縮本文の検査値です。</param>
internal sealed record JsonHistoryRecord(string Kind, string Key, string? Scope, byte[] Data, int Length, string Hash);
