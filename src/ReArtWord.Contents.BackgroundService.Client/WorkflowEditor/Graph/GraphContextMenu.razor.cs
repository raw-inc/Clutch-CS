using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>メニューを開いた文書・選択・マウス位置にだけ編集を適用します。</summary>
// 抑制理由: Razorの生成宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorの生成宣言に合わせます。")]
public sealed partial class GraphContextMenu
{
    [Inject]
    private EditorDocument Model
    {
        get; set;
    } = null!;

    /// <summary>選択または配線を編集したことを通知します。</summary>
    [Parameter]
    public EventCallback Changed
    {
        get; set;
    }

    /// <summary>固定した選択範囲を再利用レシピへ分離します。</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<string>> ExtractRequested
    {
        get; set;
    }

    private bool visible, focus;
    private WorkflowDefinition? document;
    private int generation;
    private int? wire;
    private readonly HashSet<string> selection = new(StringComparer.Ordinal);
    private NodePosition point = new(0, 0);
    private double x, y;
    private ElementReference menu;
    private bool Current => ReferenceEquals(document, Model.Draft.Document) && generation == Model.Generation;
    private string Style => FormattableString.Invariant($"left:clamp(8px,{x}px,calc(100vw - 252px));top:clamp(8px,{y}px,calc(100vh - 210px))");
    private bool CanDelete => Current && (wire is int index ? GraphDeletion.CanWire(Model, index)
                                                            : GraphDeletion.CanNodes(Model, selection));

    /// <summary>操作の基準を固定して表示します。</summary>
    public void Open(MouseEventArgs args, NodePosition position, int? wireIndex = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(position);
        document = Model.Draft.Document;
        generation = Model.Generation;
        wire = wireIndex;
        selection.Clear();
        selection.UnionWith(Model.Selection.Nodes);
        point = position;
        x = args.ClientX;
        y = args.ClientY;
        visible = focus = true;
        StateHasChanged();
    }

    /// <summary>表示後にキーボード操作の対象をメニューへ移します。</summary>
    /// <returns>DOMフォーカスの反映です。</returns>
    // 抑制理由: Blazorの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (focus)
        {
            focus = false;
            await menu.FocusAsync().ConfigureAwait(true);
        }
    }

    /// <summary>メニューを閉じます。</summary>
    public void Close()
    {
        visible = false;
        StateHasChanged();
    }

    /// <summary>Escapeで選択や文書を変更せず閉じます。</summary>
    private void Key(KeyboardEventArgs args)
    {
        string escape = "Escape";
        if (string.Equals(args.Key, escape, StringComparison.Ordinal))
        {
            Close();
        }
    }

    /// <summary>外部配線を含めず選択範囲を保持します。</summary>
    /// <returns>メニュー終了の通知です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private Task CopyAsync()
    {
        if (Current && selection.Count != 0)
        {
            Model.Selection.Clipboard.Copy(document!, selection);
        }
        return FinishAsync();
    }

    /// <summary>メニュー開始位置へ一回のUndoとして貼り付けます。</summary>
    /// <returns>選択された新ノードの通知です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private Task PasteAsync()
    {
        if (Current)
        {
            Model.Selection.Clipboard.Paste(Model, point);
        }
        return FinishAsync();
    }

    /// <summary>保存と参照更新を所有する親画面へ分離要求を渡します。</summary>
    /// <returns>分離操作の完了です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private async Task ExtractAsync()
    {
        if (Current && selection.Count != 0)
        {
            Close();
            await ExtractRequested.InvokeAsync(selection.ToArray()).ConfigureAwait(true);
        }
    }

    /// <summary>選択した全ノードと付随配線を一括削除します。</summary>
    /// <returns>削除後の選択通知です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private Task DeleteAsync()
    {
        if (CanDelete)
        {
            if (wire is int index)
            {
                GraphDeletion.Wire(Model, index);
            }
            else
            {
                GraphDeletion.Nodes(Model, selection);
            }
        }
        return FinishAsync();
    }

    /// <summary>編集操作の後に親へ再描画を通知します。</summary>
    /// <returns>通知の完了です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private Task FinishAsync()
    {
        Close();
        return Changed.InvokeAsync();
    }
}
