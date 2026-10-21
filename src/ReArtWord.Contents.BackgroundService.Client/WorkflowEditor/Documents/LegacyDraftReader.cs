using System.Text.Json;
using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>移行前画面が保存した草稿を新しいC#状態へ一度取り込みます。</summary>
internal static class LegacyDraftReader
{
    private static readonly JsonSerializerOptions options = new(ContractJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };

    /// <summary>旧画面の草稿を移し、保存先を再確認できた場合だけ元の本文を削除します。</summary>
    /// <returns>存在する未保存草稿です。</returns>
    public static async Task<EditorDraft?> MigrateAsync(BrowserStorage storage, Guid newOwner, string? cluster, string? peer, CancellationToken cancellationToken = default)
    {
        // LEGACY-FORMAT: request-editor-draft-v1 — 新草稿の保存確認後に旧本文を除去し、移行完了後に取込みを撤去します。
        ArgumentNullException.ThrowIfNull(storage);
        string? owner = await storage.ReadTextAsync("contents.editor.owner", true, cancellationToken).ConfigureAwait(false);
        string? last = await storage.ReadTextAsync("contents.editor.last", true, cancellationToken).ConfigureAwait(false);
        if (owner is null || last is null)
        {
            return null;
        }
        string key = "contents.draft." + owner + "." + last;
        string? text = await storage.ReadTextAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }
        EditorDraft? draft = Parse(text);
        if (draft is not null && await EditorDraftStorage.WriteAsync(storage, newOwner, cluster, peer, draft, cancellationToken).ConfigureAwait(false) is null)
        {
            EditorDraft? confirmed = await EditorDraftStorage.ReadAsync(storage, newOwner, cluster, peer, draft.Document.Id, cancellationToken).ConfigureAwait(false);
            if (confirmed is not null && string.Equals(ContractJson.Hash(draft), ContractJson.Hash(confirmed), StringComparison.Ordinal))
            {
                await storage.RemoveIfUnchangedAsync(key, text, cancellationToken).ConfigureAwait(false);
            }
        }
        return draft;
    }

    /// <summary>旧画面固有の共同編集情報も、新草稿の同じ意味へ引き継ぎます。</summary>
    /// <returns>未保存の変更がある草稿、またはnullです。</returns>
    private static EditorDraft? Parse(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        string dirtyKey = "dirty";
        if (!root.TryGetProperty(dirtyKey, out JsonElement dirty) || !dirty.GetBoolean())
        {
            return null;
        }
        WorkflowDefinition edited = root.GetProperty("document").Deserialize<WorkflowDefinition>(options)!;
        WorkflowDefinition baseline = root.GetProperty("base").Deserialize<WorkflowDefinition>(options)!;
        string? revision = root.GetProperty("baseRevision").GetString();
        string collaborationKey = "collaboration";
        if (!root.TryGetProperty(collaborationKey, out JsonElement collaboration) || collaboration.ValueKind != JsonValueKind.Object)
        {
            return new(edited, baseline, revision, baseline, null, revision, true, []);
        }
        WorkflowDefinition original = collaboration.GetProperty("original").Deserialize<WorkflowDefinition>(options)!;
        string pendingKey = "pending";
        DraftChangeRequest? pending = collaboration.TryGetProperty(pendingKey, out JsonElement request) && request.ValueKind == JsonValueKind.Object ? request.Deserialize<DraftChangeRequest>(options)
                                                                                                                                                     : null;
        return new(edited, baseline, revision, original, collaboration.GetProperty("sessionVersion").GetString(), collaboration.GetProperty("baseRevision").GetString(), true, collaboration.GetProperty("conflicts").Deserialize<List<EditConflict>>(options) ?? [], pending);
    }
}
