using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>新規草稿の生成と同じ所有範囲への復元を担当します。</summary>
internal static class EditorDraftNavigation
{
    /// <summary>現在の草稿を保管してから、新規文書を表示します。</summary>
    /// <returns>同じ編集世代で作成できたかです。</returns>
    internal static async Task<bool> CreateAsync(EditorDocument model, bool dashboard, CancellationToken cancellationToken = default)
    {
        int generation = model.Generation;
        await model.PersistAsync(cancellationToken).ConfigureAwait(false);
        if (generation != model.Generation)
        {
            return false;
        }
        if (model.StorageProblem is not null)
        {
            throw new InvalidOperationException(model.StorageProblem);
        }
        model.New(dashboard);
        return true;
    }

    /// <summary>新しい未保存定義を生成します。</summary>
    /// <returns>保存版と混同しない初期草稿です。</returns>
    internal static EditorDraft Create(bool dashboard = false)
    {
        WorkflowDefinition definition = new("workflow-" + Guid.NewGuid().ToString("N")[..8], "New workflow", [], []);
        if (dashboard)
        {
            definition = definition with { Id = "dashboard-" + Guid.NewGuid().ToString("N")[..8], Name = "New WFDashboard", IsDashboard = true };
        }
        return new(definition, definition, null, definition, null, null, false, []);
    }

    /// <summary>新しいWFを作らず、表示対象がない状態を表します。</summary>
    /// <returns>保存・実行対象にしない空の表示状態です。</returns>
    internal static EditorDraft Empty()
    {
        WorkflowDefinition definition = new(string.Empty, string.Empty, [], []);
        return new(definition, definition, null, definition, null, null, false, []);
    }

    /// <summary>最後に開いた草稿または明示した未選択状態を接続先ごとに復元します。</summary>
    /// <returns>復元できる表示対象です。未使用の接続先や本文がない場合は未選択にします。</returns>
    internal static async Task<EditorDraft> RestoreTargetAsync(BrowserStorage storage, Guid owner, string? cluster, string? peer, CancellationToken cancellationToken = default)
    {
        string? last = await EditorDraftStorage.ReadLastAsync(storage, cluster, peer, cancellationToken).ConfigureAwait(false);
        if (last is { Length: 0 })
        {
            return Empty();
        }
        EditorDraft? retained = last is null ? null
                                             : await EditorDraftStorage.ReadAsync(storage, owner, cluster, peer, last, cancellationToken).ConfigureAwait(false);
        return retained ?? Empty();
    }

    /// <summary>削除した現草稿だけを保存領域から解除し、表示対象を空にします。</summary>
    /// <returns>同じ所有先の草稿解除です。</returns>
    internal static async Task DiscardAsync(EditorDocument model, BrowserStorage storage, Guid owner, CancellationToken cancellationToken = default)
    {
        int generation = model.Generation;
        await EditorDraftStorage.ForgetAsync(storage, owner, model.ClusterId, model.PeerId, model.Draft.Document.Id, cancellationToken).ConfigureAwait(false);
        if (generation == model.Generation)
        {
            model.Close();
            await model.PersistAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>取得中に所有先が変わった場合は復元せず、現草稿保存を先に確認します。</summary>
    /// <returns>同じ画面の新規草稿を適用した場合だけtrueです。</returns>
    internal static async Task<bool> TryOpenAsync(EditorDocument model, BrowserStorage storage, Guid owner, string id, Action<EditorDraft> apply, CancellationToken cancellationToken = default)
    {
        await model.InitializeAsync(cancellationToken).ConfigureAwait(false);
        int generation = model.Generation;
        EditorDraft? retained = await EditorDraftStorage.ReadAsync(storage, owner, model.ClusterId, model.PeerId, id, cancellationToken).ConfigureAwait(false);
        if (retained?.BaseRevision is not null || retained is null || generation != model.Generation)
        {
            return false;
        }
        await model.PersistAsync(cancellationToken).ConfigureAwait(false);
        if (generation != model.Generation || model.StorageProblem is not null)
        {
            throw new InvalidOperationException("Current draft could not be retained. Check the selected target.");
        }
        apply(retained);
        return true;
    }
}
