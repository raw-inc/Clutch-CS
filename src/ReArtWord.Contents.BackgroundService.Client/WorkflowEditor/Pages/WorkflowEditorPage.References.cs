using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Pages;

/// <summary>表示設定と参照データの取得・切替を管理します。</summary>
public sealed partial class WorkflowEditorPage
{
    [Inject]
    private EditorRecipeDrafts Recipes
    {
        get; set;
    } = null!;

    [Inject]
    private EditorLivePolicy LivePolicy
    {
        get; set;
    } = null!;

    [Inject]
    private EditorNodeActivity Activity
    {
        get; set;
    } = null!;

    /// <summary>RemoteDesktopと同じく、experimental=trueだけで追加操作を表示します。</summary>
    [Parameter, SupplyParameterFromQuery(Name = "experimental")]
    public string? Experimental
    {
        get; set;
    }

    private bool ExperimentalEnabled => string.Equals(Experimental, "true", StringComparison.Ordinal);
    private GraphViewOptions EffectiveView => viewOptions.ForEditor(ExperimentalEnabled);

    /// <summary>URLの切替後も非公開の表示選択を通常画面へ漏らしません。</summary>
    protected override void OnParametersSet() => Collaboration.RealtimePointer = EffectiveView.RealtimePointer;

    /// <summary>クラスタ共通の自動反映だけを明示変更します。</summary>
    /// <returns>元操作の段階結果です。</returns>
    private async Task ChangeLiveAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await GuardAsync(async () =>
        {
            ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational.ClusterOperationalSaveResult? result = await LivePolicy.ChangeAsync(enabled, cancellationToken).ConfigureAwait(true);
            message = Ui.Deferred("Saved {0} / shared {1} / snapshot {2} / applied {3}", result?.Saved.Outcome, result?.Shared.Outcome, result?.Snapshot.Outcome, result?.Activation.Outcome);
        }).ConfigureAwait(true);
    }

    private bool deleteOpen;
    private EditorWorkflowTabs? workflowTabs;
    private EditorNavigator? navigator;
    private string? initialWorkflow, initialNode;
    private string? openingWorkflow, requestedErrorWorkflow;

    /// <summary>削除確定後だけ草稿とタブを解除し、他の未保存草稿を維持します。</summary>
    /// <returns>保存一覧の再取得です。</returns>
    // 抑制理由: EventCallbackの固定署名で、画面寿命の取消しを下位I/Oへ渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UI通知の固定署名です。I/Oは画面寿命のトークンで取消します。")]
    private async Task DeletedWorkflowAsync(string id)
    {
        string? next = workflowTabs?.NextVisible(id);
        await persistence.ConfigureAwait(true);
        await EditorSavedCache.ForgetAsync(Storage, Model.ClusterId, Model.PeerId, id, lifetime.Token).ConfigureAwait(true);
        await Model.DiscardAsync(lifetime.Token).ConfigureAwait(true);
        workflows.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (workflowTabs is not null)
        {
            await workflowTabs.ForgetAsync(id, lifetime.Token).ConfigureAwait(true);
        }
        deleteOpen = false;
        message = () => Ui["Workflow deleted. Saved deletion details and the snapshot ID are available in operation history."];
        Navigation.NavigateTo(EditorWorkflowLocation.WithoutTarget(Navigation), replace: true);
        if (!LocalContext)
        {
            await RefreshListAsync().ConfigureAwait(true);
        }
        if (next is not null)
        {
            await OpenWorkflowAsync(next).ConfigureAwait(true);
        }
    }

    /// <summary>タブを閉じる前に草稿を保持し、隣の表示タブか未選択画面へ移ります。</summary>
    /// <returns>草稿の保存と表示先の切替です。</returns>
    // 抑制理由: EventCallbackの固定署名でlifetime.Tokenを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UI通知の固定署名でlifetime.Tokenを使用します。")]
    private async Task HideWorkflowAsync(string id)
    {
        await persistence.ConfigureAwait(true);
        await Model.PersistAsync(lifetime.Token).ConfigureAwait(true);
        if (Model.StorageProblem is not null)
        {
            throw new InvalidOperationException(Model.StorageProblem);
        }
        if (string.Equals(id, Model.Draft.Document.Id, StringComparison.Ordinal))
        {
            string? next = workflowTabs?.NextVisible(id);
            Model.Close();
            await Model.PersistAsync(lifetime.Token).ConfigureAwait(true);
            if (Model.StorageProblem is not null)
            {
                throw new InvalidOperationException(Model.StorageProblem);
            }
            if (next is not null)
            {
                await OpenWorkflowAsync(next).ConfigureAwait(true);
            }
        }
    }

    /// <summary>右クリックしたWFを開き、既存のスナップショット付き削除確認へ渡します。</summary>
    /// <returns>対象を固定した削除確認の表示です。</returns>
    // 抑制理由: EventCallbackの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UI通知の固定署名です。")]
    private async Task RequestDeleteWorkflowAsync(string id)
    {
        await OpenWorkflowAsync(id).ConfigureAwait(true);
        deleteOpen = !busy && string.Equals(id, Model.Draft.Document.Id, StringComparison.Ordinal);
    }

    /// <summary>ブラウザーに保存した図の表示設定を読み込みます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ReadViewAsync()
    {
        try
        {
            GraphViewOptions? saved = await Storage.ReadAsync<GraphViewOptions>("contents.editor.view.v1", cancellationToken: lifetime.Token).ConfigureAwait(true);
            if (saved is not null && Enum.IsDefined(saved.WireShape))
            {
                viewOptions = saved;
            }
        }
        catch (Exception exception) when (exception is JSException or JsonException)
        {
            viewProblem = Ui["The saved view settings could not be read. Defaults are being used."];
        }
    }

    /// <summary>図の表示設定を変更してブラウザーへ保存します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ChangeViewAsync(GraphViewOptions value)
    {
        viewOptions = value;
        Collaboration.RealtimePointer = EffectiveView.RealtimePointer;
        try
        {
            await Storage.WriteAsync("contents.editor.view.v1", value, cancellationToken: lifetime.Token).ConfigureAwait(true);
            viewProblem = null;
        }
        catch (JSException)
        {
            viewProblem = Ui["Applied to this view, but the settings could not be saved on this device."];
        }
    }

    /// <summary>保存済みワークフロー一覧の先頭ページを再取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task RefreshListAsync(bool refreshCatalog = true)
    {
        if (refreshCatalog || referencesOffline)
        {
            await RefreshReferencesAsync().ConfigureAwait(true);
            return;
        }
        WorkflowListPage? page = await Api.GetAsync<WorkflowListPage>("/api/workflows", lifetime.Token).ConfigureAwait(true);
        workflows.Clear();
        workflows.AddRange(page?.Entries ?? []);
        workflowCursor = page?.Cursor;
        HashSet<string> unsaved = Recipes.Catalog.Select(node => node.Type).ToHashSet(StringComparer.Ordinal);
        await new EditorReferenceData(catalog.Where(node => !unsaved.Contains(node.Type)).ToArray(), workflows.ToArray())
            .WriteAsync(Storage, Api.SelectedClusterId, Api.SelectedPeerId, lifetime.Token).ConfigureAwait(true);
    }

    /// <summary>編集に必要なノード定義とワークフローの参照情報を取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task RefreshReferencesAsync()
    {
        List<NodeDescriptor>? descriptors = await Api.GetAsync<List<NodeDescriptor>>("/api/catalog", lifetime.Token).ConfigureAwait(true) ?? [];
        WorkflowListPage? page = await Api.GetAsync<WorkflowListPage>("/api/workflows", lifetime.Token).ConfigureAwait(true);
        IReadOnlyList<WorkflowListSummary>? saved = page?.Entries ?? [];
        workflowCursor = page?.Cursor;
        catalog.Clear();
        catalog.AddRange(descriptors);
        await Recipes.RefreshAsync(lifetime.Token).ConfigureAwait(true);
        catalog.AddRange(Recipes.Catalog.Where(item => !catalog.Any(existing => string.Equals(existing.Type, item.Type, StringComparison.Ordinal))));
        workflows.Clear();
        workflows.AddRange(saved);
        referencesOffline = false;
        await new EditorReferenceData(descriptors, saved).WriteAsync(Storage, Api.SelectedClusterId, Api.SelectedPeerId, lifetime.Token).ConfigureAwait(true);
    }

    /// <summary>表示対象の一覧または状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task LoadAsync(ChangeEventArgs e)
    {
        string id = e.Value?.ToString() ?? string.Empty;
        if (id.Length != 0)
        {
            await OpenWorkflowAsync(id).ConfigureAwait(true);
        }
    }

    /// <summary>指定されたワークフローを草稿として開きます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task OpenWorkflowAsync(string id)
    {
        if (busy || string.Equals(id, Model.Draft.Document.Id, StringComparison.Ordinal))
        {
            return;
        }

        busy = true;
        openingWorkflow = id;
        try
        {
            await GuardAsync(async () =>
            {
                if (!await Model.TryOpenDraftAsync(id, lifetime.Token).ConfigureAwait(true))
                {
                    message = Ui.Deferred(await EditorWorkflowLoader.OpenAsync(Model, Api, Storage, id, lifetime.Token).ConfigureAwait(true));
                }

                documentsOpen = false;
                if (workflowTabs is not null && string.Equals(id, Model.Draft.Document.Id, StringComparison.Ordinal))
                {
                    await workflowTabs.RevealAsync(id, lifetime.Token).ConfigureAwait(true);
                }
                Navigation.NavigateTo(EditorWorkflowLocation.WithoutTarget(Navigation), replace: true);
            }).ConfigureAwait(true);
        }
        finally
        {
            busy = false;
            openingWorkflow = null;
        }
    }

    /// <summary>現在の所有先で新しいワークフロー草稿を作成します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task NewWorkflowAsync() => CreateDocumentAsync(false, lifetime.Token);

    /// <summary>実験用メニューから新しいWFDashboardを作成します。</summary>
    /// <returns>草稿保持後の新規表示です。</returns>
    private Task NewDashboardAsync(CancellationToken cancellationToken = default)
    {
        if (ExperimentalEnabled)
        {
            return CreateDocumentAsync(true, cancellationToken);
        }
        return Task.CompletedTask;
    }

    /// <summary>選択中の草稿を保存してから新しい種類の文書へ移ります。</summary>
    /// <returns>既存草稿を破棄しない切替です。</returns>
    private Task CreateDocumentAsync(bool dashboard, CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        if (busy || !await EditorDraftNavigation.CreateAsync(Model, dashboard, cancellationToken).ConfigureAwait(true))
        {
            return;
        }
        documentsOpen = false;
        catalogOpen = true;
        Navigation.NavigateTo(EditorWorkflowLocation.WithoutTarget(Navigation), replace: true);
    });

    /// <summary>同じ対象のワークフロー一覧を次ページへ進めます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task NextWorkflowsAsync() => GuardAsync(async () =>
    {
        if (workflowCursor is null)
        {
            return;
        }

        WorkflowListPage? page = await Api.PostAsync<WorkflowListPage>("/api/workflows/query", new WorkflowListQuery(Cursor: workflowCursor), lifetime.Token).ConfigureAwait(true);
        if (page is null)
        {
            throw new InvalidOperationException(Ui["The next page could not be retrieved."]);
        }

        HashSet<string?> loaded = page.Entries.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        workflows.RemoveAll(item => loaded.Contains(item.Id));
        workflows.AddRange(page.Entries);
        workflowCursor = page.Cursor;
    });

    /// <summary>選択されたワークフローを開く処理へ渡します。</summary>
    private void SelectWorkflow()
    {
        Model.SelectedNode = null;
        Model.SelectedWire = null;
        invalid = false;
    }

    /// <summary>一回目のクリックで読込みが始まっていても、同じWFへの移動要求を保持します。</summary>
    /// <returns>WF選択と、描画後の移動要求です。</returns>
    // 抑制理由: EventCallbackの固定署名で、画面の存続期間を下位処理へ渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private async Task JumpToErrorAsync(string id)
    {
        if (busy && !string.Equals(openingWorkflow, id, StringComparison.Ordinal))
        {
            return;
        }
        requestedErrorWorkflow = id;
        if (!busy)
        {
            await OpenWorkflowAsync(id).ConfigureAwait(true);
        }
        await InvokeAsync(StateHasChanged).ConfigureAwait(true);
    }

    /// <summary>移動対象の図が描画された後で、古い別WFのノードへ飛ばずに移動します。</summary>
    /// <returns>観測済みのエラー位置への移動です。</returns>
    // 抑制理由: Blazorの固定署名で画面寿命の取消しを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        PublishNotice();
        if (!busy && navigator is not null && requestedErrorWorkflow is { } id)
        {
            requestedErrorWorkflow = null;
            if (string.Equals(id, Model.Draft.Document.Id, StringComparison.Ordinal))
            {
                if (Model.ClusterId is { } cluster && Connection.Current is { } connected)
                {
                    await RuntimeDiagnostics.RefreshAsync(cluster, connected.Peers, lifetime.Token).ConfigureAwait(true);
                }
                if (!lifetime.IsCancellationRequested && string.Equals(id, Model.Draft.Document.Id, StringComparison.Ordinal))
                {
                    await navigator.FocusErrorAsync(lifetime.Token).ConfigureAwait(true);
                }
            }
        }
    }
}
