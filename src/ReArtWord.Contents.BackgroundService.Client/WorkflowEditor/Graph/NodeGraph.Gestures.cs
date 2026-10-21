using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>ポインターとキーボードによる図の選択・移動・メニューを処理します。</summary>
public sealed partial class NodeGraph
{
    private bool resizing;
    private WorkflowDefinition? dragDocument;
    private readonly Dictionary<string, NodePosition> dragOrigins = new(StringComparer.Ordinal);
    private NodeSize? sizeOrigin, sizePreview;

    /// <summary>既存のポインター捕捉を使い、位置ではなく寸法を変更します。</summary>
    /// <returns>寸法変更の開始です。</returns>
    // 抑制理由: Razorイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Razorイベントの固定署名です。")]
    private async Task BeginResizeAsync(NodeDefinition node, PointerEventArgs e)
    {
        if (Model.Locks.Node(Model.Draft.Document.Id, node.Id))
        {
            return;
        }
        NodeSize size = CodeSize(node);
        await BeginAsync(node, e).ConfigureAwait(true);
        if (string.Equals(dragging, node.Id, StringComparison.Ordinal))
        {
            resizing = true;
            sizeOrigin = sizePreview = size;
        }
    }

    /// <summary>拡縮ハンドルはキーボードでも操作できます。</summary>
    private void ResizeKey(NodeDefinition node, KeyboardEventArgs e)
    {
        if (Model.Locks.Node(Model.Draft.Document.Id, node.Id))
        {
            return;
        }
        NodeSize size = CodeSize(node);
        int delta = e.ShiftKey ? 100
                               : 20;
        NodeSize next = e.Key switch
        {
            "ArrowLeft" => size with { Width = Math.Max(360, size.Width - delta) },
            "ArrowRight" => size with { Width = Math.Min(1600, size.Width + delta) },
            "ArrowUp" => size with { Height = Math.Max(400, size.Height - delta) },
            "ArrowDown" => size with { Height = Math.Min(2000, size.Height + delta) },
            _ => size,
        };
        if (next != size)
        {
            Model.ChangeNode(node.Id, current => current with { Size = next });
        }
    }

    /// <summary>移動するノードと開始位置を固定してドラッグを開始します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task BeginAsync(NodeDefinition node, PointerEventArgs e)
    {
        if (dragging is not null || e.Button != 0)
        {
            return;
        }

        if (e.CtrlKey)
        {
            Model.Selection.Toggle(node.Id);
            Model.SelectedWire = null;
            await SelectionChanged.InvokeAsync().ConfigureAwait(true);
            return;
        }
        if (!Model.Selection.Nodes.Contains(node.Id))
        {
            Model.SelectedNode = node.Id;
        }
        Model.SelectedWire = null;
        if (Model.Locks.Node(Model.Draft.Document.Id, node.Id))
        {
            await SelectionChanged.InvokeAsync().ConfigureAwait(true);
            return;
        }

        dragOrigins.Clear();
        foreach (NodeDefinition selected in Model.Draft.Document.Nodes.Where(item => Model.Selection.Nodes.Contains(item.Id)))
        {
            if (Model.Locks.Node(Model.Draft.Document.Id, selected.Id))
            {
                await SelectionChanged.InvokeAsync().ConfigureAwait(true);
                return;
            }
            dragOrigins[selected.Id] = Position(selected);
        }
        dragDocument = Model.Draft.Document;
        original = Position(node);
        dragging = node.Id;
        int generation = Model.Generation;
        NodePosition? point = await Script.InvokeAsync<NodePosition>("contentsBrowser.beginDrag", UiCancellation, canvas, e.ClientX, e.ClientY, e.PointerId).ConfigureAwait(true);
        if (!string.Equals(dragging, node.Id, StringComparison.Ordinal) || generation != Model.Generation || !ReferenceEquals(dragDocument, Model.Draft.Document))
        {
            await CancelDragAsync().ConfigureAwait(true);
            return;
        }

        anchor = point;
        preview = original;
        await SelectionChanged.InvokeAsync().ConfigureAwait(true);
        await DragChanged.InvokeAsync(true).ConfigureAwait(true);
    }

    /// <summary>現在のポインター位置を編集中のノード位置へ反映します。</summary>
    [JSInvokable]
    public void MovePointer(double x, double y, int version)
    {
        if (disposed || version != connectionVersion || !ReferenceEquals(renderedDocument, Model.Draft.Document) || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return;
        }
        NodePosition point = new(x, y);
        if (dragging is null || anchor is null || original is null)
        {
            Collaboration.SetPointer(point, null, null);
            return;
        }
        if (resizing && sizeOrigin is not null)
        {
            sizePreview = new(Math.Clamp(sizeOrigin.Width + point.X - anchor.X, 360, 1600), Math.Clamp(sizeOrigin.Height + point.Y - anchor.Y, 400, 2000));
            StateHasChanged();
            return;
        }
        double dx = Math.Max(-dragOrigins.Values.Min(item => item.X), point.X - anchor.X);
        double dy = Math.Max(-dragOrigins.Values.Min(item => item.Y), point.Y - anchor.Y);
        preview = new(original.X + dx, original.Y + dy);
        Collaboration.SetPointer(point, dragging, preview);
        StateHasChanged();
    }

    /// <summary>クリック・入力変更の時点だけ操作位置と選択ノードを公開します。</summary>
    /// <returns>必要な選択表示の更新です。</returns>
    // 抑制理由: JavaScriptからの画面入力コールバックです。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "画面入力コールバックの固定署名です。")]
    public async Task InteractPointerAsync(double x, double y, int version, string? node)
    {
        if (!disposed && version == connectionVersion && double.IsFinite(x) && double.IsFinite(y))
        {
            if (node is not null && Model.Draft.Document.Nodes.Any(item => string.Equals(item.Id, node, StringComparison.Ordinal)))
            {
                await SelectNodeAsync(node).ConfigureAwait(true);
            }
            Collaboration.SetPointer(new(x, y), null, null, interaction: true);
        }
    }

    /// <summary>キャンバス外へ出たカーソルだけを非表示にします。</summary>
    [JSInvokable]
    public void LeavePointer() => Collaboration.SetPointer(null, dragging, preview);

    /// <summary>ノードの移動を確定してドラッグ状態を終了します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task EndAsync(PointerEventArgs args)
    {
        if (dragging is null)
        {
            return;
        }
        int version = connectionVersion;
        NodePosition point = await ClientToGraphAsync(args.ClientX, args.ClientY).ConfigureAwait(true);
        if (disposed || version != connectionVersion || dragging is null || !ReferenceEquals(dragDocument, Model.Draft.Document))
        {
            await CancelDragAsync().ConfigureAwait(true);
            return;
        }
        MovePointer(point.X, point.Y, version);
        if (resizing && dragging is not null && sizePreview is not null)
        {
            NodeSize size = sizePreview;
            Model.ChangeNode(dragging, node => node with { Size = size });
        }
        if (!resizing && dragging is not null && preview is not null && preview != original)
        {
            double dx = preview.X - original!.X, dy = preview.Y - original.Y;
            Model.Change(document => document with { Nodes = document.Nodes.Select(node => dragOrigins.TryGetValue(node.Id, out NodePosition? origin) ? node with { Position = new(origin.X + dx, origin.Y + dy) }
                                                                                                                                                      : node).ToArray() });
        }

        dragOrigins.Clear();
        resizing = false;
        sizeOrigin = sizePreview = null;
        dragging = null;
        preview = null;
        anchor = null;
        original = null;
        Collaboration.SetPointer(point, null, null, interaction: true);
        await DragChanged.InvokeAsync(false).ConfigureAwait(true);
    }

    /// <summary>未確定の編集操作を取り消して選択状態を戻します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task CancelDragAsync()
    {
        dragOrigins.Clear();
        resizing = false;
        sizeOrigin = sizePreview = null;
        dragging = null;
        preview = null;
        anchor = null;
        original = null;
        Collaboration.SetPointer(null, null, null);
        await DragChanged.InvokeAsync(false).ConfigureAwait(true);
    }

    /// <summary>指定した接続を選択して親画面へ通知します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task SelectWireAsync(int index)
    {
        Model.SelectedWire = index;
        Model.SelectedNode = null;
        await SelectionChanged.InvokeAsync().ConfigureAwait(true);
    }

    /// <summary>選択済みノードの右クリックでは複数選択を維持します。</summary>
    /// <returns>位置を固定したメニュー表示です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private async Task OpenNodeMenuAsync(string id, MouseEventArgs args)
    {
        if (!Model.Selection.Nodes.Contains(id))
        {
            await SelectNodeAsync(id).ConfigureAwait(true);
        }
        await OpenBackgroundMenuAsync(args).ConfigureAwait(true);
    }

    /// <summary>接続だけの削除メニューを表示します。</summary>
    /// <returns>座標と文書を固定した表示です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private async Task OpenWireMenuAsync(int index, MouseEventArgs args)
    {
        await SelectWireAsync(index).ConfigureAwait(true);
        contextMenu?.Open(args, new(0, 0), index);
    }

    /// <summary>空白の右クリックも貼り付け位置として利用します。</summary>
    /// <returns>倍率とスクロールを反映した位置の表示です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private async Task OpenBackgroundMenuAsync(MouseEventArgs args)
    {
        WorkflowDefinition current = Model.Draft.Document;
        NodePosition point = await ClientToGraphAsync(args.ClientX, args.ClientY).ConfigureAwait(true);
        if (ReferenceEquals(current, Model.Draft.Document))
        {
            contextMenu?.Open(args, point);
        }
    }

    /// <summary>メニュー操作後の接続仮選択を解除します。</summary>
    /// <returns>親の選択表示の更新です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private Task ContextChangedAsync()
    {
        CancelConnection();
        return SelectionChanged.InvokeAsync();
    }

    /// <summary>入力欄や媒体を含むグラフの右クリックを、一つのメニュー経路へ渡します。</summary>
    /// <returns>対象と文書世代を確認したメニュー表示です。</returns>
    // 抑制理由: JavaScriptからの画面入力コールバックです。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "画面入力の固定署名です。")]
    public async Task OpenContextMenuAsync(string? node, int? wire, double x, double y, int version)
    {
        if (disposed || version != connectionVersion || !ReferenceEquals(renderedDocument, Model.Draft.Document) || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return;
        }
        MouseEventArgs args = new() { ClientX = x, ClientY = y };
        switch (wire, node)
        {
            case (int index, _) when 0 <= index && index < Model.Draft.Document.Wires.Count:
                await OpenWireMenuAsync(index, args).ConfigureAwait(true);
                break;
            case (_, { } id) when Model.Draft.Document.Nodes.Any(item => string.Equals(item.Id, id, StringComparison.Ordinal)):
                await OpenNodeMenuAsync(id, args).ConfigureAwait(true);
                break;
            default:
                await OpenBackgroundMenuAsync(args).ConfigureAwait(true);
                break;
        }
        StateHasChanged();
    }

    /// <summary>確定した矩形内のノードを既存の選択に追加します。</summary>
    /// <returns>親画面への選択通知です。</returns>
    // 抑制理由: JavaScriptからの画面入力コールバックです。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "画面入力の固定署名です。")]
    public async Task SelectRangeAsync(string[] ids, int version)
    {
        if (disposed || version != connectionVersion || !ReferenceEquals(renderedDocument, Model.Draft.Document))
        {
            return;
        }
        Model.Selection.Add(Model.Draft.Document.Nodes.Where(node => ids.Contains(node.Id, StringComparer.Ordinal)).Select(node => node.Id));
        Model.SelectedWire = null;
        await SelectionChanged.InvokeAsync().ConfigureAwait(true);
        StateHasChanged();
    }

    /// <summary>ノードの選択キーを編集操作へ変換します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task NodeKeyAsync(string id, KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, EnterKey, StringComparison.Ordinal))
        {
            Model.SelectedNode = id;
            Model.SelectedWire = null;
            await SelectionChanged.InvokeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>接続の選択キーを編集操作へ変換します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task WireKeyAsync(int index, KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, EnterKey, StringComparison.Ordinal))
        {
            await SelectWireAsync(index).ConfigureAwait(true);
        }
    }
}
