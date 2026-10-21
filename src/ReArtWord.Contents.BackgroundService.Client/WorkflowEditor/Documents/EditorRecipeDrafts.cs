using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>分離で生まれた草稿を先に保管し、親保存時に参照先を先に保存します。</summary>
internal sealed class EditorRecipeDrafts(EditorDocument model, BrowserStorage storage, ManagementApi api)
{
    private readonly Dictionary<string, Guid> operations = new(StringComparer.Ordinal);
    private readonly List<EditorDraft> drafts = [];

    /// <summary>まだ保存版を持たない再利用定義をタブへ提示します。</summary>
    internal IReadOnlyList<WorkflowDefinition> Unsaved => drafts.Where(draft => draft.BaseRevision is null).Select(draft => draft.Document).ToArray();

    /// <summary>草稿段階でも公開ポートを描画できます。</summary>
    internal IReadOnlyList<NodeDescriptor> Catalog => Unsaved.Select(SubworkflowRecipeDescriptor.Create).ToArray();

    /// <summary>同じ所有者・接続先の再利用草稿だけを取得します。</summary>
    /// <returns>再表示とreloadに必要な参照の更新です。</returns>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        int generation = model.Generation;
        IReadOnlyList<EditorDraft> retained = await EditorDraftStorage.ReadAllAsync(storage, model.StorageOwner, model.ClusterId, model.PeerId, cancellationToken).ConfigureAwait(true);
        if (generation != model.Generation)
        {
            return;
        }
        drafts.Clear();
        drafts.AddRange(retained.Where(draft => SubworkflowInterface.IsSubworkflow(draft.Document)));
    }

    /// <summary>親の編集前に子草稿の書込みを確認し、競合時は元の選択を残します。</summary>
    /// <returns>子草稿と置換後の親を保持する処理です。</returns>
    internal async Task ExtractAsync(GraphExtractionPlan plan, CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(plan.Original, model.Draft.Document) || !model.Locks.Allows(plan.Original, plan.Parent))
        {
            throw UiError.InvalidOperation("The selected restore scope is read-only");
        }
        Scope scope = Capture();
        int generation = scope.Generation;
        await model.PersistAsync(cancellationToken).ConfigureAwait(true);
        Ensure(scope);
        if (model.StorageProblem is not null)
        {
            throw UiError.InvalidOperation(model.StorageProblem);
        }
        EditorDraft draft = new(plan.Recipe, plan.Recipe, null, plan.Recipe, null, null, true, [], NeedsBaseline: true);
        string? problem = await EditorDraftStorage.WriteAsync(storage, scope.Owner, scope.Cluster, scope.Peer, draft, cancellationToken).ConfigureAwait(true);
        if (problem is not null)
        {
            throw UiError.InvalidOperation(problem);
        }
        if (generation != model.Generation || !ReferenceEquals(plan.Original, model.Draft.Document) || !model.Locks.Allows(plan.Original, plan.Parent))
        {
            throw UiError.InvalidOperation("The workflow changed after this menu was opened. Close it and select the item again.");
        }
        model.Change(_ => plan.Parent);
        model.SelectedNode = plan.InstanceId;
        model.SelectedWire = null;
        await model.PersistAsync(cancellationToken).ConfigureAwait(true);
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>新規レシピだけを依存順に保存し、親に未解決の参照を残しません。</summary>
    /// <returns>必要な新規定義の保存・反映確認です。</returns>
    internal async Task SaveDependenciesAsync(WorkflowDefinition parent, CancellationToken cancellationToken = default)
    {
        if (!parent.Nodes.Any(node => SubworkflowInterface.Target(node.Type) is not null))
        {
            await model.PersistAsync(cancellationToken).ConfigureAwait(true);
            return;
        }
        Scope scope = Capture();
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
        Ensure(scope);
        Dictionary<string, EditorDraft> pending = drafts.Where(draft => draft.BaseRevision is null).ToDictionary(draft => draft.Document.Id, StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal) { parent.Id };
        await SaveAsync(parent, pending, visiting, scope, cancellationToken).ConfigureAwait(true);
        await model.PersistAsync(cancellationToken).ConfigureAwait(true);
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>保存要求IDを内容ごとに保持し、不明な保存を別要求で繰り返しません。</summary>
    /// <returns>必要な子定義の保存確認です。</returns>
    private async Task SaveAsync(WorkflowDefinition parent, Dictionary<string, EditorDraft> pending, HashSet<string> visiting, Scope scope, CancellationToken cancellationToken = default)
    {
        foreach (string id in parent.Nodes.Select(node => SubworkflowInterface.Target(node.Type)).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (!pending.TryGetValue(id, out EditorDraft? draft))
            {
                continue;
            }
            if (!visiting.Add(id))
            {
                throw UiError.InvalidOperation("Recursive recipe references cannot run.");
            }
            await SaveAsync(draft.Document, pending, visiting, scope, cancellationToken).ConfigureAwait(true);
            Ensure(scope);
            string key = scope.Cluster + "/" + scope.Peer + "/" + ContractJson.Hash(draft.Document);
            if (!operations.TryGetValue(key, out Guid operation))
            {
                operations[key] = operation = Guid.NewGuid();
            }
            if (scope.Cluster is null || scope.Peer is null)
            {
                throw UiError.InvalidOperation("Select a cluster and peer before saving.");
            }
            WorkflowSaveResult? result = await api.PostForPeerAsync<WorkflowSaveResult>("/api/workflows/save", new WorkflowSaveRequest(operation, draft.Document, null), scope.Peer, scope.Cluster, cancellationToken: cancellationToken).ConfigureAwait(true);
            Ensure(scope);
            if (result?.Saved.Outcome != EOutcome.Succeeded || result.Activation.Outcome != EOutcome.Succeeded)
            {
                throw UiError.InvalidOperation("Save and activate the referenced SubWorkflow before saving its containing workflow.");
            }
            EditorDraft saved = new(draft.Document, draft.Document, result.Revision, draft.Document, null, result.Revision, false, [], LocalOnly: result.LocalOnly);
            string? problem = await EditorDraftStorage.WriteAsync(storage, scope.Owner, scope.Cluster, scope.Peer, saved, cancellationToken).ConfigureAwait(true);
            if (problem is not null)
            {
                throw UiError.InvalidOperation(problem);
            }
            pending.Remove(id);
            visiting.Remove(id);
        }
    }

    /// <summary>保存操作の所有範囲を開始時に固定します。</summary>
    /// <returns>再接続後の別対象へ書き込まない識別です。</returns>
    private Scope Capture() => new(model.StorageOwner, model.ClusterId, model.PeerId, model.Generation);

    /// <summary>非同期処理中に切り替わった編集先へ変換を適用しません。</summary>
    private void Ensure(Scope scope)
    {
        if (scope.Generation != model.Generation || !string.Equals(scope.Cluster, model.ClusterId, StringComparison.Ordinal) || !string.Equals(scope.Peer, model.PeerId, StringComparison.Ordinal))
        {
            throw UiError.InvalidOperation("The workflow changed after this menu was opened. Close it and select the item again.");
        }
    }

    /// <summary>一回の保存が属する端末内領域です。</summary>
    private sealed record Scope(Guid Owner, string? Cluster, string? Peer, int Generation);

}
