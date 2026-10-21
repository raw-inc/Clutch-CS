namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>共有された一回限りの履歴の受付を、新しいPeerでも再作成させない識別子です。</summary>
/// <param name="Scope">履歴の用途です。</param>
/// <param name="Key">元の受付キーです。</param>
public sealed record RetiredHistoryKey(string Scope, string Key);
