namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>検索した一ページと検索可能性です。</summary>
/// <param name="Entries">Entriesの指定値。</param>
/// <param name="Cursor">Cursorの指定値。</param>
/// <param name="Complete">Completeの指定値。</param>
/// <param name="UnavailablePeers">UnavailablePeersの指定値。</param>
/// <param name="Omissions">巨大な単一項目から省略した詳細。</param>
public sealed record LogQueryResult(IReadOnlyList<WorkflowLogEntry> Entries, string? Cursor, bool Complete, IReadOnlyList<string>? UnavailablePeers = null, IReadOnlyList<LogEntryOmission>? Omissions = null);
