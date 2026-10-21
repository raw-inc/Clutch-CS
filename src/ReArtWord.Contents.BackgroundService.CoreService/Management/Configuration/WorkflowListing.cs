using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;

/// <summary>定義の複製を一覧へ載せず、候補版と実効版を区別します。</summary>
internal static class WorkflowListing
{
    /// <summary>本文が大きい場合も、同じ版だけを小さい断片で読み切れます。</summary>
    /// <returns>全体ハッシュと位置を含むUTF8断片です。</returns>
    internal static async Task<WorkflowDefinitionChunk> ChunkAsync(ServiceRuntime runtime, WorkflowDefinitionChunkRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Offset < 0 || request.Length is < 1 or > 65536)
        {
            throw new ArgumentException("定義断片の位置または長さが不正です。", nameof(request));
        }
        WorkflowDefinition definition;
        string revision;
        if (request.Effective)
        {
            Runtime.Workflows.Compilation.CompiledWorkflow active = runtime.Sources.GetEffective(request.Id) ?? throw new KeyNotFoundException();
            definition = active.Definition;
            revision = active.Revision;
        }
        else
        {
            StoredWorkflow stored = await runtime.Workflows.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
            definition = stored.Definition;
            revision = stored.Revision;
        }
        if (!string.Equals(revision, request.Revision, StringComparison.Ordinal))
        {
            throw new ArgumentException("取得中に定義版が変わりました。候補を取得し直してください。", nameof(request));
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(definition, ContractJson.Options);
        if (bytes.Length <= request.Offset)
        {
            throw new ArgumentException("定義断片の位置が範囲外です。", nameof(request));
        }
        int length = Math.Min(request.Length, bytes.Length - request.Offset);
        int? next = request.Offset + length;
        if (next == bytes.Length)
        {
            next = null;
        }
        return new(revision, request.Offset, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToBase64String(bytes, request.Offset, length), next);
    }

    /// <summary>完全な定義を運べない場合は不完全な定義ではなく要約を返します。</summary>
    /// <returns>完全な候補と実効版、または明示された省略情報です。</returns>
    internal static object View(StoredWorkflow workflow, ServiceRuntime runtime)
    {
        object complete = new
        {
            saved = workflow, effective = runtime.Sources.GetEffective(workflow.Definition.Id)?.Definition,
            effectiveRevision = runtime.Sources.GetEffective(workflow.Definition.Id)?.Revision,
        };
        string reason = "workflow.details.size";
        try
        {
            if (JsonSerializer.SerializeToUtf8Bytes(complete, ContractJson.Options).Length <= 1048576)
            {
                return complete;
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            reason = "workflow.details.serialization";
        }
        return new { saved = (StoredWorkflow?)null, effective = (WorkflowDefinition?)null, summary = Summary(workflow, runtime), detailReason = reason };
    }

    /// <summary>固定長cursorと件数・バイトの両上限で一覧を取得します。</summary>
    /// <returns>軽量な一覧の一ページです。</returns>
    internal static async Task<WorkflowListPage> ReadAsync(ServiceRuntime runtime, WorkflowListQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Limit is < 1 or > 100 || query.MaximumBytes is < 16384 or > 1048576 || query.Cursor?.Length is > 64
            || query.Ids is { Count: > 64 })
        {
            throw new ArgumentException("Workflow一覧のページ条件が不正です。", nameof(query));
        }
        foreach (string id in query.Ids ?? [])
        {
            ArgumentException.ThrowIfNullOrEmpty(id);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(id.Length, 256);
        }
        IReadOnlyList<StoredWorkflow> selected;
        if (query.Ids is null)
        {
            selected = await runtime.Workflows.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            List<StoredWorkflow> matches = [];
            foreach (string id in query.Ids.Distinct(StringComparer.Ordinal))
            {
                StoredWorkflow? match = await runtime.Workflows.GetAsync(id, cancellationToken).ConfigureAwait(false);
                if (match is not null)
                {
                    matches.Add(match);
                }
            }
            selected = matches;
        }
        StoredWorkflow[] all = selected.OrderBy(static item => item.Definition.Id, StringComparer.Ordinal).ToArray();
        int start = 0;
        if (query.Cursor is not null)
        {
            int previous = Array.FindIndex(all, item => string.Equals(ContractJson.Hash(item.Definition.Id), query.Cursor, StringComparison.Ordinal));
            if (previous < 0)
            {
                throw new ArgumentException("一覧が変わったため最初のページから取得してください。", nameof(query));
            }
            start = previous + 1;
        }
        List<WorkflowListSummary> entries = [];
        string? next = null;
        foreach (StoredWorkflow workflow in all.Skip(start).Take(query.Limit))
        {
            WorkflowListSummary summary = Summary(workflow, runtime);
            entries.Add(summary);
            if (query.MaximumBytes < JsonSerializer.SerializeToUtf8Bytes(new WorkflowListPage(entries, summary.IdentifierHash), ContractJson.Options).Length)
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

    /// <summary>巨大な自由記述を明示省略し、通常識別子はそのまま残します。</summary>
    /// <returns>保存候補と実効版の識別情報です。</returns>
    internal static WorkflowListSummary Summary(StoredWorkflow workflow, ServiceRuntime runtime)
    {
        string? id = workflow.Definition.Id;
        string name = workflow.Definition.Name;
        string? reason = null;
        if (256 < id.Length)
        {
            id = null;
            reason = "workflow.identifier.omitted";
        }
        if (256 < name.Length)
        {
            name = name[..256] + "…";
            reason = "workflow.display.omitted";
        }
        return new(id, name, workflow.Revision, runtime.Sources.GetEffective(workflow.Definition.Id)?.Revision,
            workflow.SavedAt, workflow.Definition.Nodes.Count, workflow.Definition.Enabled, ContractJson.Hash(workflow.Definition.Id), reason, workflow.ActivationPolicy, workflow.LocalOnly)
            { IsSubworkflow = SubworkflowInterface.IsSubworkflow(workflow.Definition), IsDashboard = workflow.Definition.IsDashboard };
    }
}
