namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Browsing;

/// <summary>一覧の対象と有限ページ、取得不能を明示します。</summary>
/// <param name="Path">閲覧したディレクトリ。nullはルート一覧。</param>
/// <param name="Parent">許可範囲内にある親ディレクトリ。</param>
/// <param name="Entries">このページの候補。</param>
/// <param name="NextOffset">次ページ。nullは末尾。</param>
/// <param name="Problem">取得できない理由。nullは成功。</param>
public sealed record FileBrowseResult(string? Path, string? Parent, IReadOnlyList<FileBrowseEntry> Entries, int? NextOffset = null, string? Problem = null);
