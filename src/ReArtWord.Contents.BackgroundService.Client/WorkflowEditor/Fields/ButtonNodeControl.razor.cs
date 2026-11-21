using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>押下だけを親画面の手動実行へ伝え、描画や値編集では実行しません。</summary>
// 抑制理由: Razor生成コードの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言に合わせます。")]
public sealed partial class ButtonNodeControl
{
    /// <summary>ボタンの入力と表示を持つノードです。</summary>
    [Parameter, EditorRequired]
    public NodeDefinition Node
    {
        get; set;
    } = null!;

    /// <summary>保存済みの対象ノードを入口にして一件開始する操作です。</summary>
    [Parameter]
    public EventCallback<string> Pressed
    {
        get; set;
    }

    /// <summary>共有状態の確認結果です。未確認はOFFとして表示しません。</summary>
    [Parameter]
    public bool? ToggleState
    {
        get; set;
    }

    private bool IsToggle => Node.Type is "control.toggleButton";
    private string? PressedState => (IsToggle, ToggleState) switch { (true, true) => "true", (true, false) => "false", _ => null };

    private bool pressing;

    /// <summary>確定できる接続文字列を表示し、未入力には翻訳済みの既定名を使います。</summary>
    private string Label
    {
        get
        {
            NodeValue? value = Presentation.Preview(Model.Draft).Input(Node, "label");
            if (value?.Kind == EValueKind.Json && value.Json.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!;
            }
            return Ui.Format("Run {0}", Node.LogicalName ?? Ui["Button"]);
        }
    }

    /// <summary>未保存の表示を保存済み定義として実行しないため、利用できない理由を示します。</summary>
    private string? DisabledReason => UnavailableReason(Model, Node, Pressed.HasDelegate, ToggleState);

    /// <summary>表示とカードの寸法計算で、同じ無効理由を使います。</summary>
    /// <returns>操作できない理由です。実行できる場合はnullです。</returns>
    internal static string? UnavailableReason(EditorDocument model, NodeDefinition node, bool canPress, bool? toggleState)
    {
        if (!canPress)
        {
            return "Button operation is unavailable in this view.";
        }
        if (model.Locks.Node(model.Draft.Document.Id, node.Id))
        {
            return "The selected restore scope is read-only";
        }
        if (model.Draft.Dirty || model.Draft.BaseRevision is null || model.HasInvalidInputs)
        {
            return "Save and activate the workflow before running it.";
        }
        string toggle = "control.toggleButton";
        if (string.Equals(node.Type, toggle, StringComparison.Ordinal) && toggleState is null)
        {
            return "The toggle state could not be confirmed yet.";
        }
        return null;
    }

    /// <summary>受付処理中の連打を抑え、後続の完了待ちではボタンを占有しません。</summary>
    /// <returns>親画面での一回の受付処理です。</returns>
    // 抑制理由: Blazorのイベント署名に合わせ、通信取消は親画面が所有します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorのイベント署名です。親画面が通信の取消を所有します。")]
    private async Task PressAsync()
    {
        if (pressing || DisabledReason is not null)
        {
            return;
        }
        pressing = true;
        try
        {
            await Pressed.InvokeAsync(Node.Id).ConfigureAwait(true);
        }
        finally
        {
            pressing = false;
        }
    }
}
