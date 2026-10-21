using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>全体マップ、文字検索、エラーと外部リンクからの画面移動を一つの座標系へ接続します。</summary>
// 抑制理由: Razorが生成する公開コンポーネント型です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開コンポーネント型です。")]
public sealed partial class EditorNavigator
{
    [Inject] private EditorDocument Model
    {
        get; set;
    } = null!;

    [Inject] private EditorDiagnostics Diagnostics
    {
        get; set;
    } = null!;

    [Inject] private IJSRuntime Script
    {
        get; set;
    } = null!;

    /// <summary>既存のスクロールとズームを所有するキャンバスです。</summary>
    [Parameter] public ElementReference CanvasHost
    {
        get; set;
    }

    /// <summary>選択範囲に合わせた倍率を、キャンバスと同じ所有者へ通知します。</summary>
    [Parameter] public EventCallback<double> ZoomChanged
    {
        get; set;
    }

    /// <summary>検索するノードの表示名です。</summary>
    // 抑制理由: Blazorが不変のParameterを交換します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが不変のParameterを交換します。")]
    public IReadOnlyList<NodeDescriptor> Catalog
    {
        get; set;
    } = [];

    /// <summary>保存・接続先変更中は画面移動を開始しません。</summary>
    [Parameter] public bool Disabled
    {
        get; set;
    }

    /// <summary>ログなどから要求された最初の移動先です。</summary>
    [Parameter] public string? InitialNodeId
    {
        get; set;
    }

    /// <summary>マップ表示の有無に追従し、その下へ重ねる操作結果です。</summary>
    [Parameter]
    public RenderFragment? Notifications
    {
        get; set;
    }

    private readonly EditorNodeSearch search = new();
    private readonly SemaphoreSlim renderGate = new(1, 1);
    private ElementReference root, searchInput;
    private IJSObjectReference? module, binding;
    private DotNetObjectReference<EditorNavigator>? reference;
    private string query = string.Empty;
    private string? owner, consumedInitial, pendingFocus, lastError, problem;
    private bool mapVisible = true;
    private bool searchVisible, focusSearch;
    private int matchIndex;
    private (double X, double Y)? pendingMapCenter;

    // 抑制理由: 検索結果は不変の配列を交換し、描画中の配列を書き換えません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "検索結果の不変配列を交換します。")]
    private string[] matches = [];
    private static string EnterKey => "Enter";
    private static string EscapeKey => "Escape";

    /// <summary>別WFの検索と位置指定を持ち越さず、変更された定義だけを検索し直します。</summary>
    protected override void OnParametersSet()
    {
        string current = Model.ClusterId + "/" + Model.PeerId + "/" + Model.Draft.Document.Id;
        if (!string.Equals(owner, current, StringComparison.Ordinal))
        {
            owner = current;
            search.Clear();
            query = string.Empty;
            matches = [];
            lastError = problem = pendingFocus = consumedInitial = null;
            pendingMapCenter = null;
        }
        RefreshMatches(false);
        if (InitialNodeId is not null && !string.Equals(consumedInitial, InitialNodeId, StringComparison.Ordinal))
        {
            consumedInitial = InitialNodeId;
            pendingFocus = InitialNodeId;
        }
    }

    /// <summary>描画済みのノード座標でマップと移動先を更新します。</summary>
    /// <returns>ブラウザーとの同期です。</returns>
    // 抑制理由: Blazorの固定署名でUiCancellationを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名でUiCancellationを使用します。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await renderGate.WaitAsync(UiCancellation).ConfigureAwait(true);
        try
        {
            if (ComponentDisposed || string.IsNullOrEmpty(CanvasHost.Id))
            {
                return;
            }
            module ??= await Script.InvokeAsync<IJSObjectReference>("import", UiCancellation, "./editor-navigation.js").ConfigureAwait(true);
            reference ??= DotNetObjectReference.Create(this);
            binding ??= await module.InvokeAsync<IJSObjectReference>("attachEditorNavigation", UiCancellation, CanvasHost, root, reference).ConfigureAwait(true);
            await binding.InvokeVoidAsync("update", UiCancellation, mapVisible, Disabled, searchVisible ? matches
                                                                                                        : [], matches.ElementAtOrDefault(matchIndex), owner).ConfigureAwait(true);
            if (pendingMapCenter is { } point)
            {
                pendingMapCenter = null;
                await binding.InvokeVoidAsync("centerPoint", UiCancellation, point.X, point.Y).ConfigureAwait(true);
            }
            if (pendingFocus is { } id)
            {
                pendingFocus = null;
                bool found = await binding.InvokeAsync<bool>("focusNode", UiCancellation, id).ConfigureAwait(true);
                if (!found)
                {
                    problem = "editor.navigation.nodeMissing";
                    await InvokeAsync(StateHasChanged).ConfigureAwait(true);
                }
            }
            if (focusSearch)
            {
                focusSearch = false;
                await searchInput.FocusAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (ComponentDisposed)
        {
        }
        finally
        {
            renderGate.Release();
        }
    }

    /// <summary>Shift選択の範囲を既存の倍率制限で表示し、描画後に中心を合わせます。</summary>
    /// <returns>倍率と移動先を反映する描画要求です。</returns>
    // 抑制理由: JSInvokableの固定署名です。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "JSInvokableの固定署名です。")]
    public async Task FitMapAsync(double x, double y, double zoom)
    {
        if (ComponentDisposed || Disabled || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(zoom))
        {
            return;
        }
        pendingMapCenter = (x, y);
        await ZoomChanged.InvokeAsync(Math.Clamp(zoom, .05, 1.5)).ConfigureAwait(true);
        await InvokeAsync(StateHasChanged).ConfigureAwait(true);
    }

    /// <summary>文字入力と区別済みのM、Ctrl+F、Ctrl+Bだけを受け取ります。</summary>
    /// <returns>次の描画要求です。</returns>
    // 抑制理由: JSInvokableの固定署名です。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "JSInvokableの固定署名です。")]
    public Task ShortcutAsync(int action)
    {
        if (ComponentDisposed || Disabled)
        {
            return Task.CompletedTask;
        }
        if (action == 0)
        {
            mapVisible = !mapVisible;
        }
        if (action == 1)
        {
            OpenSearch();
        }
        if (action == 2)
        {
            return FocusErrorAsync(UiCancellation);
        }
        return InvokeAsync(StateHasChanged);
    }

    /// <summary>複数のエラーは文書順に巡回し、存在しないノードへ飛びません。</summary>
    /// <returns>現在の問題箇所への描画要求です。</returns>
    internal Task FocusErrorAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<string?> errors = Diagnostics.Issues(Model.ClusterId, Model.Draft.Document.Id).Select(issue => issue.NodeId).ToHashSet(StringComparer.Ordinal);
        string[] ids = Model.Draft.Document.Nodes.Where(node => errors.Contains(node.Id)).Select(node => node.Id).ToArray();
        if (ids.Length == 0)
        {
            problem = "editor.navigation.noErrors";
        }
        else
        {
            lastError = ids[(Array.IndexOf(ids, lastError) + 1) % ids.Length];
            pendingFocus = lastError;
            problem = null;
        }
        return InvokeAsync(StateHasChanged);
    }

    /// <summary>検索欄を開き、ブラウザー検索とは独立した入力先へフォーカスします。</summary>
    private void OpenSearch()
    {
        searchVisible = focusSearch = true;
        problem = null;
        RefreshMatches(false);
    }

    /// <summary>文字列を保持したまま検索表示と強調だけを閉じます。</summary>
    private void CloseSearch() => searchVisible = false;

    /// <summary>入力語を変更したときは最初の一致から表示します。</summary>
    private void SearchChanged(ChangeEventArgs args)
    {
        query = args.Value?.ToString() ?? string.Empty;
        RefreshMatches(true);
    }

    /// <summary>現在の一致順序を更新し、件数が減った場合も範囲内の位置を保ちます。</summary>
    private void RefreshMatches(bool first)
    {
        matches = search.Find(Model.Draft, Catalog, Ui, query).ToArray();
        matchIndex = first ? 0
                           : Math.Clamp(matchIndex, 0, Math.Max(0, matches.Length - 1));
        if (first)
        {
            pendingFocus = matches.ElementAtOrDefault(matchIndex);
        }
    }

    /// <summary>前後の一致へ循環し、ズーム倍率は維持します。</summary>
    private void Next(int direction)
    {
        if (matches.Length != 0)
        {
            matchIndex = (matchIndex + direction + matches.Length) % matches.Length;
            pendingFocus = matches[matchIndex];
        }
    }

    /// <summary>Enter・Shift+Enterで次前、Escapeで検索を閉じます。</summary>
    /// <returns>入力イベントの完了です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private Task SearchKeyAsync(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, EscapeKey, StringComparison.Ordinal))
        {
            CloseSearch();
        }
        if (string.Equals(args.Key, EnterKey, StringComparison.Ordinal))
        {
            Next(args.ShiftKey ? -1
                               : 1);
        }
        return Task.CompletedTask;
    }

    /// <summary>画面を離れたら観測・ショートカット・DOM参照を解放します。</summary>
    /// <returns>ブラウザー資源の解放です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(true);
        await renderGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (binding is not null)
            {
                await binding.InvokeVoidAsync("dispose").ConfigureAwait(true);
                await binding.DisposeAsync().ConfigureAwait(true);
            }
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (JSDisconnectedException)
        {
        }
        finally
        {
            reference?.Dispose();
            renderGate.Release();
            renderGate.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
