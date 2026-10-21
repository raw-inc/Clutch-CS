namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>複数取得先をまとめた有限の検索ページです。</summary>
/// <param name="Entries">日時順のログです。</param>
/// <param name="HasMore">継続位置に未読の結果があるかです。</param>
/// <param name="Complete">すべての取得先で検索を確認できたかです。</param>
/// <param name="UnconfirmedSources">検索できなかった、または部分結果の取得先です。</param>
/// <param name="Sources">固定した取得先数です。</param>
/// <param name="RetrievedSources">一度以上応答を確認できた取得先数です。</param>
public sealed record LogSearchPage(IReadOnlyList<ScopedLogEntry> Entries, bool HasMore, bool Complete,
    IReadOnlyList<string> UnconfirmedSources, int Sources, int RetrievedSources);
