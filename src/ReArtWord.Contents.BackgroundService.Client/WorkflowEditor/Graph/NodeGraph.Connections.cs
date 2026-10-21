using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>文書世代を確認した接続・選択・キーボード操作を管理します。</summary>
public sealed partial class NodeGraph
{
    /// <summary>文字編集中を除くDelete操作で、選択範囲全体またはフォーカスした接続を削除します。</summary>
    /// <returns>一回の変更と親画面への通知です。</returns>
    // 抑制理由: JavaScriptからの画面入力コールバックです。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "画面入力の固定署名です。")]
    public async Task DeleteSelectionAsync(string? node, int? wire, int version)
    {
        if (disposed || version != connectionVersion || !ReferenceEquals(renderedDocument, Model.Draft.Document) || dragging is not null || output is not null)
        {
            return;
        }
        bool? deleted = (wire, node) switch
        {
            (int index, _) => GraphDeletion.Wire(Model, index),
            (_, { } id) when !Model.Selection.Nodes.Contains(id) => GraphDeletion.Node(Model, id),
            _ when Model.Selection.Nodes.Count != 0 => GraphDeletion.Nodes(Model, Model.Selection.Nodes),
            _ when Model.SelectedWire is int selected => GraphDeletion.Wire(Model, selected),
            _ => null,
        };
        if (deleted is null)
        {
            return;
        }
        problem = deleted.Value ? null
                                : "The selected restore scope is read-only";
        await SelectionChanged.InvokeAsync().ConfigureAwait(true);
        StateHasChanged();
    }

    /// <summary>同じ文書の選択をコピーし、現在のポインター位置へ既存のUndo・ロック規則で貼り付けます。</summary>
    /// <returns>選択とキャンバス表示の反映です。</returns>
    // 抑制理由: JavaScriptからの画面入力コールバックで、外部I/Oを開始しません。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "JavaScriptからの画面入力コールバックで、外部I/Oを開始しません。")]
    public async Task ClipboardShortcutAsync(bool paste, double x, double y, int version)
    {
        if (disposed || version != connectionVersion || !ReferenceEquals(renderedDocument, Model.Draft.Document)
            || dragging is not null || output is not null || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return;
        }
        if (paste)
        {
            Model.Selection.Clipboard.Paste(Model, new(x, y));
        }
        else
        {
            if (Model.Selection.Nodes.Count == 0)
            {
                return;
            }
            Model.Selection.Clipboard.Copy(Model.Draft.Document, Model.Selection.Nodes);
        }
        contextMenu?.Close();
        await ContextChangedAsync().ConfigureAwait(true);
        StateHasChanged();
    }

    /// <summary>ポインター捕捉で宛先が変わっても、押した配置の内部表示を開きます。</summary>
    /// <returns>同じ文書に属する配置だけの表示要求です。</returns>
    // 抑制理由: JavaScriptからのUI入力コールバックです。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "JavaScriptからのUI入力コールバックです。")]
    public Task OpenRecipePreviewAsync(string id, int version)
    {
        NodeDefinition? node = Model.Draft.Document.Nodes.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (disposed || version != connectionVersion || node is null || SubworkflowInterface.Target(node.Type) is null)
        {
            return Task.CompletedTask;
        }
        return PreviewRequested.InvokeAsync(id);
    }

    /// <summary>接続中の端点と基準文書を破棄して待機状態へ戻します。</summary>
    private void CancelConnection()
    {
        output = null;
        pendingDocument = null;
        problem = null;
    }

    /// <summary>キャンバスのキー操作で接続や移動を取り消します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task CanvasKeyAsync(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, EscapeKey, StringComparison.Ordinal))
        {
            contextMenu?.Close();
            CancelConnection();
            await CancelDragAsync().ConfigureAwait(true);
        }
    }

    /// <summary>近接ポートのスナップ候補を、実際の接続と同じ規則で読取り専用検査します。</summary>
    /// <returns>同じ表示文書で接続可能な場合だけtrueです。</returns>
    [JSInvokable]
    public bool CanConnectPorts(string node, string port, bool outbound, string otherNode, string otherPort, int version)
    {
        if (disposed || version != connectionVersion || !ReferenceEquals(renderedDocument, Model.Draft.Document))
        {
            return false;
        }
        WireDefinition wire = outbound ? new(node, port, otherNode, otherPort)
                                       : new(otherNode, otherPort, node, port);
        return NodeInputEditing.CanConnect(Model, wire, Catalog, renderedDocument);
    }

    /// <summary>ブラウザーが示す端点と文書世代を照合して接続を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    public async Task ConnectPortsAsync(string node, string port, bool outbound, string otherNode, string otherPort, int version)
    {
        CancelConnection();
        if (version == connectionVersion && ReferenceEquals(renderedDocument, Model.Draft.Document))
        {
            WireDefinition? wire = outbound ? new WireDefinition(node, port, otherNode, otherPort)
                                            : new WireDefinition(otherNode, otherPort, node, port);
            Connect(wire);
        }

        await InvokeAsync(StateHasChanged).ConfigureAwait(true);
    }

    /// <summary>ブラウザー側の接続操作を取り消して表示を更新します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    public async Task CancelPortConnectionAsync()
    {
        CancelConnection();
        await InvokeAsync(StateHasChanged).ConfigureAwait(true);
    }

    /// <summary>指定したノードを選択して親画面へ通知します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task SelectNodeAsync(string id)
    {
        if (Model.Selection.Nodes.Contains(id))
        {
            return;
        }

        Model.SelectedNode = id;
        Model.SelectedWire = null;
        await SelectionChanged.InvokeAsync().ConfigureAwait(true);
    }

    /// <summary>入力ポートのキー操作を接続または取消へ変換します。</summary>
    private void InputKey(string node, string port, KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, EscapeKey, StringComparison.Ordinal))
        {
            CancelConnection();
            return;
        }

        if (string.Equals(args.Key, EnterKey, StringComparison.Ordinal))
        {
            Input(node, port);
        }
    }

    /// <summary>出力ポートのキー操作を接続開始または取消へ変換します。</summary>
    private void OutputKey(string node, string port, KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, EscapeKey, StringComparison.Ordinal))
        {
            CancelConnection();
            return;
        }

        if (string.Equals(args.Key, EnterKey, StringComparison.Ordinal))
        {
            Output(node, port);
        }
    }

    /// <summary>編集保護を確認して接続元ポートと文書世代を保持します。</summary>
    private void Output(string node, string port)
    {
        if (Model.Locks.Node(Model.Draft.Document.Id, node))
        {
            return;
        }

        output = (node, port);
        pendingDocument = Model.Draft.Document;
        connectionGeneration = Model.Generation;
        problem = null;
    }

    /// <summary>接続開始時の世代と保護範囲を確認して入力ポートへ接続します。</summary>
    private void Input(string node, string port)
    {
        if (output is not { } from)
        {
            return;
        }

        output = null;
        if (connectionGeneration != Model.Generation || !ReferenceEquals(pendingDocument, Model.Draft.Document))
        {
            return;
        }

        if (Model.Locks.Node(Model.Draft.Document.Id, node) || Model.Locks.Node(Model.Draft.Document.Id, from.Node))
        {
            problem = "Connections touching restoring nodes are read-only.";
            return;
        }

        Connect(new(from.Node, from.Port, node, port));
    }

    /// <summary>型と文書の整合性を確認した接続だけを草稿へ反映します。</summary>
    private void Connect(WireDefinition wire)
    {
        if (!NodeInputEditing.Connect(Model, wire, Catalog, renderedDocument))
        {
            problem = "These ports are incompatible, unavailable, or read-only.";
            return;
        }

        problem = null;
    }
}
