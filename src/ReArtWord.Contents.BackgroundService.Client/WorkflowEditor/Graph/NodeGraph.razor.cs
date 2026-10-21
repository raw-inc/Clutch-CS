using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>ワークフロー図の配置・選択・接続とブラウザーの入力を接続します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class NodeGraph : IAsyncDisposable
{
    /// <summary>押下されたノードを明示入口にした実行を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<string> ButtonPressed
    {
        get; set;
    }

    /// <summary>親画面が確認した共有トグルの表示状態です。</summary>
    // 抑制理由: Blazorが描画ごとに不変の状態一覧を置換します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "親画面が確認した不変の表示状態を受け取ります。")]
    public IReadOnlyDictionary<string, bool?> ToggleStates
    {
        get; set;
    } = System.Collections.Immutable.ImmutableDictionary<string, bool?>.Empty;

    private static string EscapeKey => "Escape";
    private static string MatchProperties => "properties";
    private static string EnterKey => "Enter";

    [Inject]
    private EditorDiagnostics Diagnostics
    {
        get; set;
    } = null!;

    [Inject]
    private EditorPreviews Previews
    {
        get; set;
    } = null!;

    [Inject]
    private EditorDocument Model
    {
        get;
        set;
    } = null!;

    [Inject]
    private IJSRuntime Script
    {
        get;
        set;
    } = null!;

    [Inject]
    private EditorCollaboration Collaboration
    {
        get; set;
    } = null!;

    /// <summary>図の接続線とカードの表示設定です。</summary>
    [Parameter]
    public GraphViewOptions View
    {
        get;
        set;
    } = new();

    /// <summary>キャンバスの現在の表示倍率です。</summary>
    [Parameter]
    public double Zoom
    {
        get;
        set;
    } = 1;

    /// <summary>表示倍率の変更を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<double> ZoomChanged
    {
        get;
        set;
    }

    private IJSObjectReference? gestureModule, gestures;
    private readonly GraphRegionPresentation regions = new();
    private IJSObjectReference? connectionModule, connections;
    private DotNetObjectReference<NodeGraph>? gestureReference;

    /// <summary>図のDOMへ移動・接続操作を登録し、倍率とメニューのフォーカスを反映します。</summary>
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

            if (firstRender)
            {
                gestureModule = await Script.InvokeAsync<IJSObjectReference>("import", "./graph-gestures.js").ConfigureAwait(true);
                if (disposed)
                {
                    return;
                }

                gestureReference = DotNetObjectReference.Create(this);
                gestures = await gestureModule.InvokeAsync<IJSObjectReference>("attachGraphGestures", canvas, gestureReference, Zoom).ConfigureAwait(true);
                if (disposed)
                {
                    return;
                }

                connectionModule = await Script.InvokeAsync<IJSObjectReference>("import", "./graph-connections.js").ConfigureAwait(true);
                if (disposed)
                {
                    return;
                }

                connections = await connectionModule.InvokeAsync<IJSObjectReference>("attachGraphConnections", canvas, gestureReference).ConfigureAwait(true);
                if (disposed)
                {
                    return;
                }
            }

            if (gestures is not null)
            {
                await gestures.InvokeVoidAsync("update", UiCancellation, Zoom).ConfigureAwait(true);
            }

            if (connections is not null)
            {
                await connections.InvokeVoidAsync("update", UiCancellation).ConfigureAwait(true);
            }

        }
        finally
        {
            renderGate.Release();
        }
    }

    /// <summary>ブラウザーから通知された倍率を有効範囲に収めて反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    public async Task ZoomAtAsync(double value)
    {
        if (!double.IsFinite(value))
        {
            return;
        }

        await ZoomChanged.InvokeAsync(Math.Clamp(value, .05, 1.5)).ConfigureAwait(true);
        await InvokeAsync(StateHasChanged).ConfigureAwait(true);
    }

    /// <summary>図の背景クリックでノード・接続の選択を解除します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    public async Task BackgroundClickAsync()
    {
        CancelConnection();
        Model.SelectedNode = null;
        Model.SelectedWire = null;
        await SelectionChanged.InvokeAsync().ConfigureAwait(true);
        await InvokeAsync(StateHasChanged).ConfigureAwait(true);
    }

    /// <summary>図のブラウザーイベントとJavaScript参照、.NETコールバック参照を解放します。</summary>
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
                try
                {
                    if (connections is not null)
                    {
                        await connections.InvokeVoidAsync("dispose").ConfigureAwait(true);
                        await connections.DisposeAsync().ConfigureAwait(true);
                    }

                    if (connectionModule is not null)
                    {
                        await connectionModule.DisposeAsync().ConfigureAwait(true);
                    }

                    if (gestures is not null)
                    {
                        await gestures.InvokeVoidAsync("dispose").ConfigureAwait(true);
                        await gestures.DisposeAsync().ConfigureAwait(true);
                    }

                    if (gestureModule is not null)
                    {
                        await gestureModule.DisposeAsync().ConfigureAwait(true);
                    }
                }
                catch (JSDisconnectedException)
                {
                }
                finally
                {
                    gestureReference?.Dispose();
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

    /// <summary>ノードカードの詳細入力欄を表示するかどうかです。</summary>
    [Parameter]
    public bool ShowDetails
    {
        get;
        set;
    } = true;

    /// <summary>編集に使用するノード定義の一覧です。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<NodeDescriptor> Catalog
    {
        get;
        set;
    } = [];

    /// <summary>ノードまたは接続の選択変更を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback SelectionChanged
    {
        get;
        set;
    }

    /// <summary>選択範囲の分離を草稿の保存を所有する親へ通知します。</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<string>> ExtractRequested
    {
        get; set;
    }

    /// <summary>配置単位の内部実行プレビューを開きます。</summary>
    [Parameter]
    public EventCallback<string> PreviewRequested
    {
        get; set;
    }

    /// <summary>図のドラッグ中状態を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<bool> DragChanged
    {
        get;
        set;
    }

    private int connectionGeneration;
    private int connectionVersion;
    private WorkflowDefinition? renderedDocument, pendingDocument;

    /// <summary>親から渡された値に合わせて表示用の状態を更新します。</summary>
    protected override void OnParametersSet()
    {
        GraphTypeInference current = Presentation.Graph(Model.Draft.Document, Catalog);
        if (!ReferenceEquals(inference, current) || renderedDetails != ShowDetails || !string.Equals(renderedLanguage, DisplayLanguage, StringComparison.Ordinal))
        {
            portLists.Clear();
            portNames.Clear();
        }
        inference = current;
        renderedDetails = ShowDetails;
        renderedLanguage = DisplayLanguage;
        choicesVersion = Choices.Version;
        GraphRenderInputs next = GraphRenderInputs.Read(Model, inference, View, Zoom, ShowDetails, DisplayLanguage, Diagnostics, Previews, choicesVersion, DisplayTimeZone.Id, ConcurrencyQueues.Version);
        skipParentRender = next == renderInputs.Graph && ReferenceEquals(renderInputs.Toggles, ToggleStates);
        renderInputs = (next, ToggleStates);
        if (!ReferenceEquals(renderedDocument, Model.Draft.Document))
        {
            renderedDocument = Model.Draft.Document;
            connectionVersion++;
            CancelConnection();
        }

        if (output is not null && connectionGeneration != Model.Generation)
        {
            CancelConnection();
        }
    }

    private ElementReference canvas;
    private string? dragging;
    private NodePosition? preview;
    private NodePosition? anchor;
    private NodePosition? original;
    private (string Node, string Port)? output;
    private string? problem;
    private GraphContextMenu? contextMenu;

    private GraphTypeInference inference = null!;
    private (GraphRenderInputs? Graph, IReadOnlyDictionary<string, bool?>? Toggles) renderInputs;
    private bool skipParentRender;
    private int choicesVersion;

    /// <summary>同じ親入力の定期通知を省き、選択・ドラッグなど自身のイベントは描画します。</summary>
    /// <returns>表示に関係する更新かどうかです。</returns>
    protected override bool ShouldRender()
    {
        bool render = !skipParentRender;
        skipParentRender = false;
        return render;
    }

    private bool renderedDetails;
    private string? renderedLanguage;
    private readonly Dictionary<(NodeDescriptor? Descriptor, string? Node, bool Outbound), List<string>> portLists = [];
    private readonly Dictionary<(string Node, string Port, bool Outbound), string> portNames = [];
    private readonly SemaphoreSlim renderGate = new(1, 1);
    private bool disposed;
}
