namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>有限件数のディレクトリ一覧です。</summary>
/// <param name="Entries">Entriesの指定値。</param>
/// <param name="Continuation">Continuationの指定値。</param>
public sealed record FileListPage(IReadOnlyList<FileEntryInfo> Entries, string? Continuation);
