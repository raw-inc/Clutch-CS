using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;

/// <summary>離脱中のローカル版を共有版と区別し、再参加後の比較選択へ接続します。</summary>
internal static class DetachedWorkflowManagement
{
    /// <summary>本文一覧を配らず、明示した比較と元操作照会を登録します。</summary>
    public static void Register(ManagementRegistry registry, ServiceRuntime runtime)
    {
        registry.Read("workflows.detached", async (value, cancellation) =>
        {
            DetachedWorkflowListQuery query = new();
            if (value is not null)
            {
                query = ManagementRegistry.Body<DetachedWorkflowListQuery>(value);
            }
            return await ListAsync(runtime, query, cancellation).ConfigureAwait(false);
        });
        registry.Read("workflows.detached-detail", async (value, cancellation) => await DetailAsync(runtime, ManagementRegistry.Id(value), cancellation).ConfigureAwait(false));
        registry.Write("workflows.detached-resolve", async (value, cancellation) => await runtime.Workflows.ResolveDetachedAsync(ManagementRegistry.Body<DetachedWorkflowResolution>(value), cancellation).ConfigureAwait(false));
        registry.Read("workflows.detached-operations", async (value, cancellation) => await runtime.Workflows.GetSaveOperationAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
    }

    /// <summary>元版とローカル版の変更を跨いだ継続位置は受け付けません。</summary>
    /// <returns>比較対象のメタデータページです。</returns>
    private static async Task<DetachedWorkflowListPage> ListAsync(ServiceRuntime runtime, DetachedWorkflowListQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Limit is < 1 or > 100 || 100 < query.Cursor?.Length)
        {
            throw new ManagementInputException();
        }
        DetachedWorkflowState[] all = (await runtime.Workflows.GetDetachedAsync(cancellationToken).ConfigureAwait(false)).OrderBy(item => item.WorkflowId, StringComparer.Ordinal).ToArray();
        string fingerprint = ContractJson.Hash(new { runtime.Cluster.LocalInstanceId, States = all.Select(item => new { item.WorkflowId, item.Local.Revision, Current = item.Current?.Revision }).ToArray() });
        int start = ManagementPageCursor.Read(query.Cursor, fingerprint, all.Length);
        List<DetachedWorkflowListSummary> entries = [];
        foreach (DetachedWorkflowState item in all.Skip(start).Take(query.Limit))
        {
            entries.Add(new(item.WorkflowId, item.Local.Definition.Name, item.Local.Revision, item.Base?.Revision, item.Current?.Revision,
                item.Conflicts.Count, item.IntegrationOperationId, item.Integration?.Shared.Outcome));
            if (1048576 < JsonSerializer.SerializeToUtf8Bytes(entries, ContractJson.Options).Length)
            {
                entries.RemoveAt(entries.Count - 1);
                if (entries.Count == 0)
                {
                    throw new ManagementInputException("局所変更metadataが一覧容量を超えています。");
                }
                break;
            }
        }
        return new(entries, ManagementPageCursor.Next(fingerprint, start + entries.Count, all.Length));
    }

    /// <summary>選択した一件の完全な候補だけを比較へ渡します。</summary>
    /// <returns>取得した内容、または未確認理由です。</returns>
    private static async Task<DetachedWorkflowDetail> DetailAsync(ServiceRuntime runtime, string id, CancellationToken cancellationToken = default)
    {
        DetachedWorkflowState? state = (await runtime.Workflows.GetDetachedAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => string.Equals(item.WorkflowId, id, StringComparison.Ordinal));
        if (state is null)
        {
            return new(null, "Detached.CandidateSetChanged");
        }
        DetachedWorkflowDetail detail = new(state);
        string? reason = ManagementPayload.Problem(detail);
        if (reason is not null)
        {
            return new(null, "Detached." + reason);
        }
        return detail;
    }
}
