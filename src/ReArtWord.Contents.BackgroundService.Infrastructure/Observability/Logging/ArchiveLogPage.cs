using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>ログの記録日時にかかわらず、最初のページに含まれた保存ファイルだけを順に検索します。</summary>
internal static class ArchiveLogPage
{
    /// <summary>通常の検索カーソルとアーカイブの保存範囲を区別します。</summary>
    internal static string Prefix => "archive:";

    /// <summary>各ページのカーソルに、このCoreでの登録順の上限と対象ファイル数を保持します。</summary>
    /// <returns>取得範囲の一ページです。保持期限で対象ファイルが失われた場合は欠落を返します。</returns>
    internal static async Task<LogQueryResult> ReadAsync(LogStore store, IReadOnlyList<LogSegment> segments, LogQuery query,
        IReadOnlyList<string> patterns, CancellationToken cancellationToken = default)
    {
        long lastSequence = 0;
        if (string.Equals(query.Cursor, Prefix, StringComparison.Ordinal))
        {
            query = query with { Cursor = null };
            if (segments.Count != 0)
            {
                lastSequence = segments.Max(segment => segment.IndexSequence);
            }
        }
        else
        {
            string[] parts = query.Cursor![Prefix.Length..].Split('/', 3);
            if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out lastSequence)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
            {
                throw new InvalidDataException("アーカイブのログ取得位置が不正です。");
            }
            segments = segments.Where(segment => segment.IndexSequence <= lastSequence).ToArray();
            if (segments.Count != count)
            {
                return new([], null, false);
            }
            query = query with { Cursor = parts[2] };
        }
        LogQueryResult result = await LogSearch.ScanAsync(store, segments, query, patterns, cancellationToken).ConfigureAwait(false);
        if (result.Cursor is null)
        {
            return result;
        }
        return result with { Cursor = string.Create(CultureInfo.InvariantCulture, $"{Prefix}{lastSequence}/{segments.Count}/{result.Cursor}") };
    }
}
