using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

/// <summary>ワークフローの確認情報を現有効版と共有反映希望へ結び付けます。</summary>
internal static class WorkflowImpact
{
    /// <summary>未保存候補を保存成功と表示せず、実際に反映する候補を比較します。</summary>
    /// <returns>同じPeerの候補と有効版に固定された影響情報です。</returns>
    internal static async Task<ConfigurationImpactPreview> InspectAsync(WorkflowStore store, IDurableRepository repository, string peer, string id, CancellationToken cancellationToken = default)
    {
        StoredWorkflow? unsaved = store.Availability?.TryGetCandidate(EStoragePurpose.Configuration, "workflow:" + id)?.Value?.Json.Deserialize<StoredWorkflow>(ContractJson.Options);
        StoredWorkflow saved = unsaved ?? await store.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        DurableEntry? marker = await repository.ReadAsync("workflow.activation", id, cancellationToken).ConfigureAwait(false);
        return Create(saved, store.GetEffective?.Invoke(id)?.Definition, marker?.Version, peer, unsaved is not null);
    }

    /// <summary>同じ候補・現有効版・反映希望・Peerの確認を識別します。</summary>
    /// <returns>反映直前に再計算できる比較識別子です。</returns>
    internal static string Token(StoredWorkflow saved, WorkflowDefinition? effective, string? marker, string peer, bool unsaved = false)
        => ContractJson.Hash(new { saved.Revision, effective, marker, peer, saved.LocalOnly, unsaved });

    /// <summary>ノードをID辞書へ写し、配列順の変化をノード設定の差分と混同しません。</summary>
    /// <returns>表示用の差分と確認できる適用範囲です。</returns>
    internal static ConfigurationImpactPreview Create(StoredWorkflow saved, WorkflowDefinition? effective, string? marker, string peer, bool unsaved = false)
    {
        (IReadOnlyList<ConfigurationDifference> differences, bool truncated) = ConfigurationDifferences.Compare(Normalize(effective), Normalize(saved.Definition), saved.Definition.ContainsPersonalData || effective?.ContainsPersonalData == true);
        string? effectiveRevision = null;
        if (effective is not null)
        {
            effectiveRevision = ContractJson.Hash(effective);
        }
        return new(saved.Definition.Id, saved.Revision, effectiveRevision, Token(saved, effective, marker, peer, unsaved), differences,
            ["Applies to new inputs; already started inputs retain their configuration.", "Changes: " + string.Join(", ", WorkflowChangeInspection.Kinds(effective, saved.Definition))],
            ["External applications, devices and physical side effects cannot be predicted from configuration alone."], truncated, unsaved);
    }

    /// <summary>比較する定義の表示用コピーを作成します。</summary>
    /// <returns>識別子から各ノード設定を辿れる比較対象です。</returns>
    private static JsonObject? Normalize(WorkflowDefinition? definition)
    {
        if (definition is null)
        {
            return null;
        }
        JsonObject document = JsonSerializer.SerializeToNode(definition, ContractJson.Options)!.AsObject();
        JsonObject nodes = new();
        foreach (NodeDefinition node in definition.Nodes)
        {
            nodes[node.Id] = JsonSerializer.SerializeToNode(node, ContractJson.Options);
        }
        document["nodes"] = nodes;
        return document;
    }
}
