using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Catalog;

/// <summary>検索とキーボード操作からノードを指定位置へ追加します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class NodeQuickAdd : IAsyncDisposable
{
    private static string EnterKey => "Enter";
    private static string EscapeKey => "Escape";
    private static string TabKey => "Tab";
    private static string ArrowRightKey => "ArrowRight";
    private static string ArrowDownKey => "ArrowDown";
    private static string MatchArrowLeft => "ArrowLeft";
    private static string MatchArrowUp => "ArrowUp";

    [Inject]
    private IJSRuntime Script
    {
        get;
        set;
    } = null!;

    /// <summary>追加位置を解決するキャンバスのDOM参照です。</summary>
    [Parameter]
    public ElementReference CanvasHost
    {
        get;
        set;
    }

    /// <summary>編集に使用するノード定義の一覧です。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<NodeDescriptor> Catalog
    {
        get;
        set;
    } = [];

    /// <summary>ノード追加操作を無効にするかどうかです。</summary>
    [Parameter]
    public bool Disabled
    {
        get;
        set;
    }

    /// <summary>選択したノードの追加を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<NodePlacementRequest> Add
    {
        get;
        set;
    }

    private readonly string listId = "quick-add-" + Guid.NewGuid().ToString("N");
    private DotNetObjectReference<NodeQuickAdd>? reference;
    private IJSObjectReference? module, binding;
    private ElementReference popup, searchInput;
    private string? hostId, category;
    private string search = string.Empty;

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyList<NodeDescriptor> matches = [];
    private NodeDescriptor? selected;
    private bool open, focusSearch, disposed;
    private double clientX, clientY;
    private IEnumerable<string> Categories => Catalog.Select(item => item.Category).Distinct(StringComparer.Ordinal).OrderBy(GraphPresentation.CategoryOrder);
    private string? ActiveId => selected is null ? null
                                                 : OptionId(selected);

    /// <summary>候補の選択とアクセシビリティに使うDOM識別子を作ります。</summary>
    /// <returns>この検索パネル内で候補を識別するDOM IDです。</returns>
    private string OptionId(NodeDescriptor item) => listId + "-" + Uri.EscapeDataString(item.Type);

    /// <summary>親から渡された値に合わせて表示用の状態を更新します。</summary>
    protected override void OnParametersSet()
    {
        if (open)
        {
            RefreshMatches();
        }
    }

    /// <summary>候補パネルのキー操作を登録し、表示時の入力フォーカスを反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed)
        {
            return;
        }

        await renderGate.WaitAsync(UiCancellation).ConfigureAwait(true);
        try
        {
            if (disposed)
            {
                return;
            }

            module ??= await Script.InvokeAsync<IJSObjectReference>("import", "./editor-interaction.js").ConfigureAwait(true);
            if (disposed)
            {
                return;
            }

            if (!string.Equals(hostId, CanvasHost.Id, StringComparison.Ordinal) && !string.IsNullOrEmpty(CanvasHost.Id))
            {
                if (binding is not null)
                {
                    await using (binding.ConfigureAwait(true))
                    {
                        await binding.InvokeVoidAsync("dispose", UiCancellation).ConfigureAwait(true);
                    }
                }

                reference ??= DotNetObjectReference.Create(this);
                binding = await module.InvokeAsync<IJSObjectReference>("attachQuickAdd", CanvasHost, reference).ConfigureAwait(true);
                if (disposed)
                {
                    return;
                }

                hostId = CanvasHost.Id;
            }

            if (binding is not null)
            {
                await binding.InvokeVoidAsync("setEnabled", UiCancellation, !Disabled).ConfigureAwait(true);
                if (open && Disabled)
                {
                    await CloseAsync().ConfigureAwait(true);
                    return;
                }

                if (open)
                {
                    await binding.InvokeVoidAsync("show", UiCancellation, popup, searchInput, clientX, clientY, focusSearch).ConfigureAwait(true);
                    focusSearch = false;
                }
            }
        }
        finally
        {
            renderGate.Release();
        }
    }

    /// <summary>追加位置と編集対象を固定してノード検索を開きます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    public Task OpenAtAsync(double x, double y)
    {
        if (Disabled || disposed || open || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return Task.CompletedTask;
        }

        clientX = x;
        clientY = y;
        search = string.Empty;
        category = null;
        selected = null;
        open = true;
        focusSearch = true;
        RefreshMatches();
        return InvokeAsync(StateHasChanged);
    }

    /// <summary>選択したノード候補と追加位置を親画面へ通知します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task AddAsync(NodeDescriptor descriptor)
    {
        if (!open || Disabled)
        {
            return;
        }

        NodePlacementRequest? placement = new NodePlacementRequest(descriptor, clientX, clientY);
        await CloseAsync().ConfigureAwait(true);
        await Add.InvokeAsync(placement).ConfigureAwait(true);
    }

    /// <summary>検索パネルを閉じてブラウザーのフォーカスを戻します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    public async Task CloseAsync()
    {
        if (!open)
        {
            return;
        }

        open = false;
        if (binding is not null)
        {
            await binding.InvokeVoidAsync("close", UiCancellation).ConfigureAwait(true);
        }

        if (!disposed)
        {
            await InvokeAsync(StateHasChanged).ConfigureAwait(true);
        }
    }

    /// <summary>候補パネルのブラウザーイベントと相互参照を解放します。</summary>
    /// <returns>所有する処理と参照の解放完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    public override async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await renderGate.WaitAsync().ConfigureAwait(true);
        try
        {
            try
            {
                disposed = true;
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
                }
            }
            finally
            {
                await base.DisposeAsync().ConfigureAwait(true);
                GC.SuppressFinalize(this);
            }
        }
        finally
        {
            renderGate.Release();
            renderGate.Dispose();
        }
    }

    private readonly SemaphoreSlim renderGate = new(1, 1);
}
