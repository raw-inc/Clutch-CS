namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>実ログを削除せず、ページ転送時だけ省略した範囲です。</summary>
/// <param name="EntryId">元ログの識別子。</param>
/// <param name="Fields">省略または短縮した項目。</param>
/// <param name="Reason">省略理由。</param>
public sealed record LogEntryOmission(Guid EntryId, IReadOnlyList<string> Fields, string Reason);
