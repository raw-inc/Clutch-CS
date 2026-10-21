using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;

/// <summary>閲覧中は文章として表示し、確定時だけコメント草稿を変更します。</summary>
// 抑制理由: Razorが生成する公開コンポーネント型です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partialの型です。")]
public sealed partial class WfDashboardComment
{
    [Inject]
    private EditorDocument Model
    {
        get; set;
    } = null!;

    /// <summary>このWFDashboardが所有するコメントの配置です。</summary>
    [Parameter, EditorRequired]
    public NodeDefinition Node
    {
        get; set;
    } = null!;

    /// <summary>保存処理などによる一時的な編集禁止です。</summary>
    [Parameter]
    public bool Locked
    {
        get; set;
    }

    private ElementReference input, label;
    private bool editing, focusInput, focusLabel;
    private int generation;
    private string text = string.Empty, original = string.Empty;
    private string? editingId;
    private string Text
    {
        get
        {
            if (WfDashboardLayout.TryReadComment(Node.Settings, out string value))
            {
                return value;
            }
            return string.Empty;
        }
    }

    private bool ReadOnly => Locked || Model.Locks.Node(Model.Draft.Document.Id, Node.Id);

    /// <summary>定期更新は入力中の文字を交換せず、他の編集による変更だけを優先します。</summary>
    protected override void OnParametersSet()
    {
        if (editing && (generation != Model.Generation || !string.Equals(editingId, Node.Id, StringComparison.Ordinal)
            || !string.Equals(original, Text, StringComparison.Ordinal) || ReadOnly))
        {
            editing = focusInput = focusLabel = false;
        }
    }

    /// <summary>入力開始とEscapeによる取消しでだけフォーカスを移します。</summary>
    /// <returns>画面内のフォーカス設定です。</returns>
    // 抑制理由: Blazorの固定署名で、画面内の要素だけを操作します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名で画面内の要素だけを操作します。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (focusInput || focusLabel)
        {
            ElementReference target = label;
            if (focusInput)
            {
                target = input;
            }
            focusInput = focusLabel = false;
            await target.FocusAsync(preventScroll: true).ConfigureAwait(true);
        }
    }

    /// <summary>現在の文字列を比較元として編集を開始します。</summary>
    private void Begin()
    {
        if (ReadOnly)
        {
            return;
        }
        generation = Model.Generation;
        editingId = Node.Id;
        text = original = Text;
        editing = focusInput = true;
    }

    /// <summary>Escapeは本文を変更せず表示へ戻し、Enterは通常の改行として扱います。</summary>
    private void Key(KeyboardEventArgs args)
    {
        string escape = "Escape";
        if (string.Equals(args.Key, escape, StringComparison.Ordinal))
        {
            editing = false;
            focusLabel = true;
        }
    }

    /// <summary>同じ文書・同じ本文が残る場合だけ、一回のUndoとして確定します。</summary>
    private void Apply()
    {
        if (!editing)
        {
            return;
        }
        editing = false;
        NodeDefinition? current = Model.Draft.Document.Nodes.FirstOrDefault(node => string.Equals(node.Id, editingId, StringComparison.Ordinal));
        if (ReadOnly || generation != Model.Generation || current is null
            || !WfDashboardLayout.TryReadComment(current.Settings, out string previous) || !string.Equals(original, previous, StringComparison.Ordinal)
            || string.Equals(text, previous, StringComparison.Ordinal) || WfDashboardLayout.MaximumCommentLength < text.Length)
        {
            return;
        }
        Model.ChangeNode(current.Id, node => node with { Settings = WfDashboardLayout.CommentSettings(text) });
    }
}
