using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>現在の草稿を維持しながらワークフローの切替候補を表示します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class EditorWorkflowTabs
{
    [Inject]
    private BrowserStorage Storage
    {
        get; set;
    } = null!;

    /// <summary>選択草稿を保持してから、指定したタブの表示を閉じます。</summary>
    [Parameter]
    public EventCallback<string> Hide
    {
        get; set;
    }

    /// <summary>指定WFを選択し、既存の削除確認を開きます。</summary>
    [Parameter]
    public EventCallback<string> Delete
    {
        get; set;
    }

    /// <summary>指定WFを開いてエラーのあるノードへ移動します。</summary>
    [Parameter]
    public EventCallback<string> FocusError
    {
        get; set;
    }

    private string? menuId, dragged, dropTarget, preferenceProblem;
    private string? lastSelection;
    private double menuX, menuY;
    private bool focusMenu;
    private bool layoutNeedsSave;
    private bool draftsConfirmed;
    private ElementReference menu;
    private string LayoutKey => "contents.editor.tabs.v1." + JsonSerializer.Serialize(new[] { Model.ClusterId, Model.PeerId });
    private string MenuStyle => FormattableString.Invariant($"left:clamp(8px,{menuX}px,calc(100vw - 240px));top:clamp(8px,{menuY}px,calc(100vh - 220px))");

    internal IReadOnlyList<EditorWorkflowTab> All => retained.All;

    /// <summary>閉じる対象以外で、近い位置の表示中タブを探します。</summary>
    /// <returns>次に開くタブ、または未選択です。</returns>
    internal string? NextVisible(string id) => retained.NextVisible(id);

    /// <summary>同じクラスタ・Peerのタブ設定だけを復元します。</summary>
    /// <returns>表示設定の読取りです。</returns>
    // 抑制理由: Blazorの固定署名でUiCancellationを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名でUiCancellationを使用します。")]
    protected override async Task OnInitializedAsync()
    {
        try
        {
            EditorWorkflowTabLayout? saved = await Storage.ReadAsync<EditorWorkflowTabLayout>(LayoutKey, cancellationToken: UiCancellation).ConfigureAwait(true);
            if (saved is not null)
            {
                retained.Restore(saved);
            }
            retained.Recover(await EditorDraftStorage.ReadAllAsync(Storage, Model.StorageOwner, Model.ClusterId, Model.PeerId, UiCancellation).ConfigureAwait(true));
            draftsConfirmed = true;
        }
        catch (Exception exception) when (exception is JSException or JsonException)
        {
            preferenceProblem = "tabs.readFailed";
        }
    }

    /// <summary>親WFから分離し、まだ保存版を持たないレシピです。</summary>
    // 抑制理由: Blazorが不変の一覧をParameterとして交換します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが不変の一覧をParameterとして交換します。")]
    public IReadOnlyList<WorkflowDefinition> DraftRecipes
    {
        get; set;
    } = [];

    private readonly HashSet<string> recipeTabs = new(StringComparer.Ordinal);
    [Inject]
    private EditorDiagnostics Diagnostics
    {
        get; set;
    } = null!;

    [Inject]
    private EditorDocument Model
    {
        get; set;
    } = null!;

    /// <summary>選択候補として取得したワークフローの一覧です。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<WorkflowListSummary> Items
    {
        get;
        set;
    } = [];

    /// <summary>現在編集中のワークフロー識別子です。</summary>
    [Parameter]
    public string SelectedId
    {
        get;
        set;
    } = string.Empty;

    /// <summary>現在編集中のワークフローの表示名です。</summary>
    [Parameter]
    public string SelectedName
    {
        get;
        set;
    } = string.Empty;

    /// <summary>現在の草稿に未保存の変更があるかどうかです。</summary>
    [Parameter]
    public bool Dirty
    {
        get;
        set;
    }

    /// <summary>現在の草稿がローカルだけに保持されているかどうかです。</summary>
    [Parameter]
    public bool LocalOnly
    {
        get;
        set;
    }

    /// <summary>候補一覧に次のページがあるかどうかです。</summary>
    [Parameter]
    public bool HasMore
    {
        get;
        set;
    }

    /// <summary>オンラインの保存一覧を取得できた場合だけ、一覧外の古いタブを整理します。</summary>
    [Parameter]
    public bool ListConfirmed
    {
        get; set;
    }

    /// <summary>関連する取得または操作が進行中かどうかです。</summary>
    [Parameter]
    public bool Busy
    {
        get;
        set;
    }

    /// <summary>選択したワークフローを開く処理を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<string> Open
    {
        get;
        set;
    }

    /// <summary>新しいワークフローの作成を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback New
    {
        get;
        set;
    }

    /// <summary>実験機能として追加メニューを表示するかです。</summary>
    [Parameter]
    public bool Experimental
    {
        get; set;
    }

    /// <summary>元の草稿を保持してWFDashboardを作成します。</summary>
    [Parameter]
    public EventCallback NewDashboard
    {
        get; set;
    }

    /// <summary>次の候補ページの取得を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback Next
    {
        get;
        set;
    }

    private readonly EditorWorkflowTabState retained = new();

    /// <summary>削除確定後のタブを保持しません。</summary>
    /// <returns>削除済みタブを次回へ戻さない表示設定の保存です。</returns>
    internal Task ForgetAsync(string id, CancellationToken cancellationToken = default)
    {
        retained.Forget(id);
        return SaveLayoutAsync(cancellationToken);
    }

    /// <summary>一覧から選択された非表示タブを明示的に戻します。</summary>
    /// <returns>表示設定の保存です。</returns>
    internal Task RevealAsync(string id, CancellationToken cancellationToken = default)
    {
        retained.Reveal(id);
        return SaveLayoutAsync(cancellationToken);
    }

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyList<EditorWorkflowTab> tabs = [];

    /// <summary>親から渡された値に合わせて表示用の状態を更新します。</summary>
    protected override void OnParametersSet() => RebuildTabs();

    /// <summary>保存済み候補と草稿を現在の表示順・非表示設定へ反映します。</summary>
    private void RebuildTabs()
    {
        if (!string.Equals(lastSelection, SelectedId, StringComparison.Ordinal))
        {
            retained.Reveal(SelectedId);
            lastSelection = SelectedId;
        }
        foreach (string id in recipeTabs.Where(id => !DraftRecipes.Any(item => string.Equals(item.Id, id, StringComparison.Ordinal))))
        {
            retained.Forget(id);
        }
        recipeTabs.Clear();
        foreach (WorkflowDefinition recipe in DraftRecipes)
        {
            retained.Remember(recipe);
            recipeTabs.Add(recipe.Id);
        }
        layoutNeedsSave |= retained.Prune(Items, SelectedId, ListConfirmed && draftsConfirmed && !HasMore);
        tabs = retained.Update(Items, SelectedId, SelectedName, Dirty, LocalOnly, SubworkflowInterface.IsSubworkflow(Model.Draft.Document), Model.Draft.Document.IsDashboard);
    }

    /// <summary>クリックしたタブを固定してメニューを開きます。</summary>
    private void OpenMenu(string id, MouseEventArgs args)
    {
        if (!Busy)
        {
            menuId = id;
            menuX = args.ClientX;
            menuY = args.ClientY;
            focusMenu = true;
        }
    }

    /// <summary>表示後にメニューをキーボード操作の対象にします。</summary>
    /// <returns>フォーカスの移動です。</returns>
    // 抑制理由: Blazorの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (layoutNeedsSave)
        {
            layoutNeedsSave = false;
            await SaveLayoutAsync(UiCancellation).ConfigureAwait(true);
        }
        if (focusMenu)
        {
            focusMenu = false;
            await menu.FocusAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Escapeでタブ操作を取り消します。</summary>
    private void MenuKey(KeyboardEventArgs args)
    {
        string escape = "Escape";
        if (string.Equals(args.Key, escape, StringComparison.Ordinal))
        {
            menuId = null;
        }
    }

    /// <summary>草稿保持が完了したときだけタブを隠します。</summary>
    /// <returns>非表示設定の保存です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名でUiCancellationを使用します。")]
    private async Task HideAsync()
    {
        if (Busy || menuId is not { } id)
        {
            return;
        }
        menuId = null;
        try
        {
            await Hide.InvokeAsync(id).ConfigureAwait(true);
            retained.Hide(id);
            RebuildTabs();
            await SaveLayoutAsync(UiCancellation).ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            preferenceProblem = "tabs.hideFailed";
        }
    }

    /// <summary>WF本体の削除は既存の確認ダイアログへ委譲します。</summary>
    /// <returns>削除確認の表示です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private Task DeleteAsync()
    {
        string? id = menuId;
        menuId = null;
        return !Busy && id is not null ? Delete.InvokeAsync(id)
                                       : Task.CompletedTask;
    }

    /// <summary>メニューの移動先を可視タブの範囲に限定します。</summary>
    /// <returns>隣の表示タブまたは端です。</returns>
    private string? Neighbor(int offset) => retained.Neighbor(menuId, offset);

    /// <summary>ドラッグまたはメニューからの並替えを保存します。</summary>
    /// <returns>表示順の更新です。</returns>
    private async Task MoveAsync(string? id, string? target, CancellationToken cancellationToken = default)
    {
        menuId = dragged = dropTarget = null;
        if (!Busy && id is not null && target is not null)
        {
            retained.Move(id, target);
            RebuildTabs();
            await SaveLayoutAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>表示だけの保存失敗を通知し、現在の画面の選択は維持します。</summary>
    /// <returns>ブラウザー設定の保存確認です。</returns>
    private async Task SaveLayoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Storage.WriteAsync(LayoutKey, retained.Export(), cancellationToken: cancellationToken).ConfigureAwait(true);
            preferenceProblem = null;
        }
        catch (JSException)
        {
            preferenceProblem = "tabs.writeFailed";
        }
    }
}
