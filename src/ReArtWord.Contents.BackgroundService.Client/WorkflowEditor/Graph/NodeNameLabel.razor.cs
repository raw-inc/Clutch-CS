using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>論理名の入力中だけ文字編集を所有し、確定時に一回のUndoへまとめます。</summary>
// 抑制理由: Razorが生成する公開コンポーネント型です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partialの型です。")]
public sealed partial class NodeNameLabel
{
    private static string EnterKey => "Enter";
    private static string EscapeKey => "Escape";
    private static string RenameKey => "F2";

    [Inject]
    private EditorDocument Model
    {
        get; set;
    } = null!;

    /// <summary>表示対象の配置です。</summary>
    [Parameter, EditorRequired]
    public NodeDefinition Node
    {
        get; set;
    } = null!;

    /// <summary>未設定時にも識別できる種類の表示情報です。</summary>
    [Parameter]
    public NodeDescriptor? Descriptor
    {
        get; set;
    }

    /// <summary>ノードの寸法に収める見出し幅です。</summary>
    [Parameter]
    public double Width
    {
        get; set;
    }

    private ElementReference input, label;
    private bool editing, focusInput, focusLabel;
    private int generation, renderedGeneration;
    private string name = string.Empty;
    private string? editingId, originalName;
    private bool ReadOnly => Model.Locks.Node(Model.Draft.Document.Id, Node.Id);

    /// <summary>別文書や他の編集者が変更した名前へ、遅れた入力を上書きしません。</summary>
    protected override void OnParametersSet()
    {
        renderedGeneration = Model.Generation;
        if (editing && (generation != renderedGeneration || !string.Equals(editingId, Node.Id, StringComparison.Ordinal)
            || !string.Equals(originalName, Node.LogicalName, StringComparison.Ordinal) || ReadOnly))
        {
            editing = focusInput = focusLabel = false;
        }
    }

    /// <summary>編集の開始・キーボード確定後だけフォーカスを移し、マウスで離れた先は奪いません。</summary>
    /// <returns>対象要素へのフォーカス設定です。</returns>
    // 抑制理由: Blazorの固定署名で、画面内の要素だけを操作します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名で、画面内の要素だけを操作します。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (focusInput || focusLabel)
        {
            ElementReference target = focusInput ? input
                                                 : label;
            focusInput = focusLabel = false;
            await target.FocusAsync(preventScroll: true).ConfigureAwait(true);
        }
    }

    /// <summary>現在の配置に対してだけ論理名の編集を開始します。</summary>
    private void Begin()
    {
        NodeDefinition? current = Model.Draft.Document.Nodes.FirstOrDefault(node => string.Equals(node.Id, Node.Id, StringComparison.Ordinal));
        if (ReadOnly || renderedGeneration != Model.Generation || current is null || !string.Equals(current.LogicalName, Node.LogicalName, StringComparison.Ordinal))
        {
            return;
        }
        generation = Model.Generation;
        editingId = Node.Id;
        originalName = Node.LogicalName;
        name = originalName ?? string.Empty;
        editing = focusInput = true;
    }

    /// <summary>入力途中は文書やUndo履歴を書き換えません。</summary>
    private void InputChanged(ChangeEventArgs args) => name = args.Value?.ToString() ?? string.Empty;

    /// <summary>EnterとF2でも見出しを編集できます。</summary>
    private void LabelKey(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, EnterKey, StringComparison.Ordinal) || string.Equals(args.Key, RenameKey, StringComparison.Ordinal))
        {
            Begin();
        }
    }

    /// <summary>IME変換を終えたEnterで確定し、Escapeでは開始前の名前を維持します。</summary>
    private void EditKey(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, EnterKey, StringComparison.Ordinal) || string.Equals(args.Key, EscapeKey, StringComparison.Ordinal))
        {
            focusLabel = true;
            if (string.Equals(args.Key, EnterKey, StringComparison.Ordinal))
            {
                Apply();
            }
            else
            {
                editing = false;
            }
        }
    }

    /// <summary>同じ文書・同じ名前の比較元が残る場合だけ、任意の表示情報を確定します。</summary>
    private void Apply()
    {
        if (!editing)
        {
            return;
        }
        editing = false;
        NodeDefinition? current = Model.Draft.Document.Nodes.FirstOrDefault(node => string.Equals(node.Id, editingId, StringComparison.Ordinal));
        if (generation != Model.Generation || current is null || !string.Equals(current.LogicalName, originalName, StringComparison.Ordinal))
        {
            focusLabel = false;
            return;
        }
        string? value = name.Trim();
        if (value.Length == 0)
        {
            value = null;
        }
        Model.ChangeNode(current.Id, node => node with { LogicalName = value });
    }
}
