using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Conflicts;

/// <summary>候補全本文による一覧応答の肥大化を避け、取得不能も明示します。</summary>
internal static class ConflictListing
{
    private static int PageBytes => 1024 * 1024;

    /// <summary>候補集合が変わった継続位置を拒否し、同じ版の有限一覧を返します。</summary>
    /// <returns>値を含まない一ページです。</returns>
    public static async Task<ConflictListPage> ReadAsync(ServiceRuntime runtime, ConflictListQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Limit is < 1 or > 100 || 100 < query.Cursor?.Length)
        {
            throw new ManagementInputException();
        }
        StateConflict[] all = (await runtime.Conflicts.GetAsync(cancellationToken).ConfigureAwait(false)).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        string fingerprint = ContractJson.Hash(new { runtime.Cluster.LocalInstanceId, Candidates = all.Select(item => new { item.Id, Versions = item.Candidates.Select(candidate => candidate.Version).Order(StringComparer.Ordinal).ToArray() }).ToArray() });
        int start = ManagementPageCursor.Read(query.Cursor, fingerprint, all.Length);
        List<ConflictListSummary> entries = [];
        foreach (StateConflict conflict in all.Skip(start).Take(query.Limit))
        {
            string? reason = DetailProblem(conflict);
            entries.Add(new(conflict.Id, conflict.Scope, conflict.Key, conflict.Candidates.Select(candidate => candidate.Version).ToArray(),
                conflict.WorkflowIds, conflict.OperationalFields, conflict.ValuesHidden, conflict.Resolvable, reason is null, reason));
            if (PageBytes < JsonSerializer.SerializeToUtf8Bytes(entries, ContractJson.Options).Length)
            {
                entries.RemoveAt(entries.Count - 1);
                if (entries.Count == 0)
                {
                    throw new ManagementInputException("競合metadataが一覧容量を超えています。");
                }
                break;
            }
        }
        return new(entries, ManagementPageCursor.Next(fingerprint, start + entries.Count, all.Length));
    }

    /// <summary>比較のために選択した一件を取得します。</summary>
    /// <returns>完全な候補、または詳細取得不能の理由です。</returns>
    public static async Task<ConflictDetail> DetailAsync(ServiceRuntime runtime, ConflictDetailRequest request, CancellationToken cancellationToken = default)
    {
        StateConflict? conflict = (await runtime.Conflicts.GetAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => string.Equals(item.Id, request.Id, StringComparison.Ordinal));
        if (conflict is null)
        {
            return new(null, "Conflict.CandidateSetChanged");
        }
        string? reason = DetailProblem(conflict);
        if (reason is null)
        {
            return new(conflict);
        }
        return new(null, reason);
    }

    /// <summary>候補全体が比較用の応答上限に収まるか検証します。</summary>
    /// <returns>明示した省略理由、またはnullです。</returns>
    private static string? DetailProblem(StateConflict conflict)
    {
        string? reason = ManagementPayload.Problem(new ConflictDetail(conflict));
        if (reason is not null)
        {
            return "Conflict." + reason;
        }
        return null;
    }
}
