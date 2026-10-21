using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.Management;

/// <summary>完全なワークフローだけを編集へ渡し、巨大本文は版固定で分割取得します。</summary>
internal static class WorkflowReader
{
    /// <summary>保存前の版比較には対象一件の要約だけを取得し、定義本文を転送しません。</summary>
    /// <returns>要求した識別子と一致する保存版の情報です。</returns>
    internal static async Task<WorkflowListSummary> ReadSummaryAsync(ManagementApi api, string id, CancellationToken cancellationToken = default)
    {
        string? peer = api.SelectedPeerId, cluster = api.SelectedClusterId;
        WorkflowListPage? page = await api.PostForPeerAsync<WorkflowListPage>("/api/workflows/query", new WorkflowListQuery(Ids: [id]), peer, cluster, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (page?.Entries.Count != 1 || !string.Equals(page.Entries[0].Id, id, StringComparison.Ordinal) || page.Cursor is not null)
        {
            throw UiError.InvalidOperation("Saved workflow version information is unavailable.");
        }
        return page.Entries[0];
    }

    /// <summary>単体の保存版と実効版を必要な場合だけ完全取得します。</summary>
    /// <returns>検証済み定義を持つ表示情報です。</returns>
    internal static async Task<WorkflowView> ReadAsync(ManagementApi api, string id, CancellationToken cancellationToken = default)
    {
        string? peer = api.SelectedPeerId;
        string? cluster = api.SelectedClusterId;
        WorkflowView? view = await api.GetForPeerAsync<WorkflowView>("/api/workflows/" + Uri.EscapeDataString(id), peer, cluster, cancellationToken).ConfigureAwait(false);
        if (view?.Saved is not null)
        {
            return view;
        }
        WorkflowListSummary summary = view?.Summary ?? throw UiError.Attach(new InvalidOperationException("Saved workflow version information is unavailable."), "Saved workflow version information is unavailable.");
        WorkflowDefinition saved = await ReadDefinitionAsync(api, id, summary.SavedRevision, false, peer, cluster, cancellationToken).ConfigureAwait(false);
        WorkflowDefinition? effective = null;
        if (summary.EffectiveRevision is string revision)
        {
            effective = await ReadDefinitionAsync(api, id, revision, true, peer, cluster, cancellationToken).ConfigureAwait(false);
        }
        return new(new StoredWorkflow(saved, summary.SavedRevision, summary.SavedAt, summary.SavedActivationPolicy, summary.LocalOnly), effective, summary.EffectiveRevision, summary);
    }

    /// <summary>同版・同ハッシュの全断片を検査するまで定義として公開しません。</summary>
    /// <returns>欠落のない完全定義です。</returns>
    private static async Task<WorkflowDefinition> ReadDefinitionAsync(ManagementApi api, string id, string revision, bool effective, string? peer, string? cluster, CancellationToken cancellationToken = default)
    {
        using MemoryStream bytes = new();
        int offset = 0;
        int? total = null;
        string? hash = null;
        do
        {
            WorkflowDefinitionChunkRequest request = new(id, revision, effective, offset);
            WorkflowDefinitionChunk? chunk = await api.PostForPeerAsync<WorkflowDefinitionChunk>("/api/workflows/definition-chunk", request, peer, cluster, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (chunk is null || !string.Equals(chunk.Revision, revision, StringComparison.Ordinal) || chunk.Offset != offset || chunk.TotalBytes < 1 || 67108864 < chunk.TotalBytes
                || total is not null && total != chunk.TotalBytes || hash is not null && !string.Equals(hash, chunk.Hash, StringComparison.Ordinal))
            {
                throw UiError.Attach(new InvalidOperationException("The workflow chunks have inconsistent revisions, offsets or lengths, or exceed the 64 MiB browser editing limit. The received content will not be used for editing."), "The workflow chunks have inconsistent revisions, offsets or lengths, or exceed the 64 MiB browser editing limit. The received content will not be used for editing.");
            }
            total = chunk.TotalBytes;
            hash = chunk.Hash;
            byte[] data = Convert.FromBase64String(chunk.DataBase64);
            if (data.Length == 0 || 65536 < data.Length || total - offset < data.Length)
            {
                throw UiError.Attach(new InvalidOperationException("The workflow chunk has an invalid length."), "The workflow chunk has an invalid length.");
            }
            bytes.Write(data);
            offset += data.Length;
            int? next = null;
            if (offset < total)
            {
                next = offset;
            }
            if (chunk.NextOffset != next)
            {
                throw UiError.Attach(new InvalidOperationException("The next workflow chunk offset does not match."), "The next workflow chunk offset does not match.");
            }
        } while (offset < total);
        byte[] content = bytes.ToArray();
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)), hash, StringComparison.OrdinalIgnoreCase))
        {
            throw UiError.Attach(new InvalidOperationException("The workflow document checksum does not match."), "The workflow document checksum does not match.");
        }
        WorkflowDefinition definition = JsonSerializer.Deserialize<WorkflowDefinition>(content, ContractJson.Options) ?? throw UiError.Attach(new InvalidOperationException("The workflow document is missing."), "The workflow document is missing.");
        if (!string.Equals(definition.Id, id, StringComparison.Ordinal))
        {
            throw UiError.Attach(new InvalidOperationException("The workflow document ID does not match."), "The workflow document ID does not match.");
        }
        return definition;
    }
}
