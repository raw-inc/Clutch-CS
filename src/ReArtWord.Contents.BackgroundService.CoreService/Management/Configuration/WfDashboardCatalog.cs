using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;

/// <summary>保存済みWFとSubWorkflowから対応ノードの識別だけを検索します。</summary>
internal sealed class WfDashboardCatalog(WorkflowStore workflows, NodeRegistry registry)
{
    /// <summary>追加操作時だけ検索し、入力値やWF全体をブラウザーへ送信しません。</summary>
    /// <returns>名前一致を優先した有限ページです。</returns>
    internal async Task<WfDashboardCatalogPage> ReadAsync(WfDashboardCatalogQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Search is null || 256 < query.Search.Length || query.Offset is < 0 or > 100000 || query.Limit is < 1 or > 100)
        {
            throw new ArgumentException("Invalid WFDashboard catalog query.", nameof(query));
        }
        string search = query.Search.Trim();
        List<(WfDashboardCatalogItem Item, int Rank)> matches = [];
        foreach (StoredWorkflow workflow in await workflows.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (workflow.Definition.IsDashboard)
            {
                continue;
            }
            foreach (NodeDefinition node in workflow.Definition.Nodes.Where(node => WfDashboardReference.Supports(node.Type)))
            {
                string type = registry.Find(node.Type)?.Descriptor.DisplayName ?? node.Type;
                string[] fields = [node.LogicalName ?? string.Empty, node.Id, type, node.Type, workflow.Definition.Name];
                if (search.Length != 0 && !fields.Any(field => field.Contains(search, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                int rank = 2;
                if (search.Length != 0 && fields.Any(field => string.Equals(field, search, StringComparison.OrdinalIgnoreCase)))
                {
                    rank = 0;
                }
                if (search.Length != 0 && rank != 0 && fields.Take(3).Any(field => field.StartsWith(search, StringComparison.OrdinalIgnoreCase)))
                {
                    rank = 1;
                }
                WfDashboardCatalogItem item = new(new(workflow.Definition.Id, node.Id, node.Type), Short(workflow.Definition.Name), Short(type), Short(node.LogicalName));
                matches.Add((item, rank));
            }
        }
        WfDashboardCatalogItem[] page = matches.OrderBy(item => item.Rank).ThenBy(item => item.Item.Reference.WorkflowId, StringComparer.Ordinal)
            .ThenBy(item => item.Item.Reference.NodeId, StringComparer.Ordinal).Skip(query.Offset).Take(query.Limit + 1).Select(item => item.Item).ToArray();
        int? next = null;
        if (query.Limit < page.Length)
        {
            next = query.Offset + query.Limit;
        }
        return new(page.Take(query.Limit).ToArray(), next);
    }

    /// <summary>一覧の自由記述だけを有限にし、物理参照は切り詰めません。</summary>
    /// <returns>検索候補用の短い表記です。</returns>
    private static string Short(string? text)
    {
        if (text is { Length: > 256 })
        {
            return text[..256] + "…";
        }
        return text ?? string.Empty;
    }
}
