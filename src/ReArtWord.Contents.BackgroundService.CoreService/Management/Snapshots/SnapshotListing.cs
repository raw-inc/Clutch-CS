using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Snapshots;

/// <summary>復元候補の本文を通信へ複製せず、有限メタデータページを返します。</summary>
internal static class SnapshotListing
{
    /// <summary>既存カタログの保存内容を変えずに軽量ページへ投影します。</summary>
    /// <returns>一ページと明示的な続き位置です。</returns>
    internal static async Task<SnapshotListPage> ReadAsync(ServiceRuntime runtime, SnapshotListQuery query, CancellationToken cancellationToken = default)
    {
        return Build(await runtime.SnapshotCatalog.ListAsync(cancellationToken).ConfigureAwait(false), query);
    }

    /// <summary>本文の大きさによらない候補ページを生成します。</summary>
    /// <returns>バイト上限内で進捗するページです。</returns>
    internal static SnapshotListPage Build(IReadOnlyList<SnapshotRecord> records, SnapshotListQuery query)
    {
        if (query.Limit is < 1 or > 100 || query.MaximumBytes is < 16384 or > 1048576 || query.Cursor?.Length is > 64)
        {
            throw new ArgumentException("snapshot一覧のページ条件が不正です。", nameof(query));
        }
        SnapshotRecord[] all = records.Where(item => (query.PeerId is null || string.Equals(item.PeerId, query.PeerId, StringComparison.Ordinal))
            && (query.TargetId is null || string.Equals(item.TargetId, query.TargetId, StringComparison.Ordinal)) && (query.Kind is null || item.Kind == query.Kind))
            .OrderByDescending(item => item.CapturedAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        int start = 0;
        if (query.Cursor is not null)
        {
            int previous = Array.FindIndex(all, item => string.Equals(ContractJson.Hash(item.Id), query.Cursor, StringComparison.Ordinal));
            if (previous < 0)
            {
                throw new ArgumentException("一覧または絞込みが変わりました。先頭ページから取得してください。", nameof(query));
            }
            start = previous + 1;
        }
        List<SnapshotListSummary> entries = [];
        string? next = null;
        foreach (SnapshotRecord record in all.Skip(start).Take(query.Limit))
        {
            SnapshotListSummary summary = Summary(record, query.MaximumBytes);
            entries.Add(summary);
            if (query.MaximumBytes < JsonSerializer.SerializeToUtf8Bytes(new SnapshotListPage(entries, summary.IdentifierHash), ContractJson.Options).Length)
            {
                entries.RemoveAt(entries.Count - 1);
                break;
            }
            next = summary.IdentifierHash;
        }
        if (start + entries.Count == all.Length)
        {
            next = null;
        }
        return new(entries, next);
    }

    /// <summary>巨大な単一メタデータもページを塞がず、選択不能の省略として返します。</summary>
    /// <returns>本文を参照しない識別情報です。</returns>
    private static SnapshotListSummary Summary(SnapshotRecord record, int maximumBytes)
    {
        SnapshotListSummary summary = new(record.Id, record.TargetId, record.PeerId, record.Kind, record.CapturedAt, record.Hash, record.NodeIds, ContractJson.Hash(record.Id), Permanent: record.Permanent);
        if (JsonSerializer.SerializeToUtf8Bytes(new SnapshotListPage([summary], summary.IdentifierHash), ContractJson.Options).Length <= maximumBytes)
        {
            return summary;
        }
        return summary with
        {
            Id = Bounded(record.Id), TargetId = Bounded(record.TargetId), PeerId = Bounded(record.PeerId), Hash = Bounded(record.Hash), NodeIds = null,
            ScopeOmitted = record.NodeIds is not null, DetailReason = "snapshot.metadata.size",
        };
    }

    /// <summary>過大な識別子を別の識別子へ切り詰めません。</summary>
    /// <returns>完全な識別子か明示省略のnullです。</returns>
    private static string? Bounded(string value)
    {
        if (256 < value.Length)
        {
            return null;
        }
        return value;
    }
}
