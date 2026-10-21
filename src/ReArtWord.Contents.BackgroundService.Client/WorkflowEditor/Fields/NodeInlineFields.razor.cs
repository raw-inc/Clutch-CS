using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>ドラッグとズームだけで設定欄やネットワーク候補を再構築しません。</summary>
// 抑制理由: Razorが生成する公開partial宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partial宣言に合わせます。")]
public sealed partial class NodeInlineFields
{
    /// <summary>保存されているノードの設定と入力です。</summary>
    [Parameter, EditorRequired]
    public NodeDefinition Node
    {
        get; set;
    } = null!;

    /// <summary>接続から確定したポートと編集用の設定スキーマです。</summary>
    [Parameter, EditorRequired]
    public NodeDescriptor Descriptor
    {
        get; set;
    } = null!;

    /// <summary>復元中のノードは設定値を変えず編集だけを無効にします。</summary>
    [Parameter]
    public bool ReadOnly
    {
        get; set;
    }

    /// <summary>再起動・再接続・候補再取得を、設定値の変更とは分けて反映します。</summary>
    [Parameter]
    public int ChoicesVersion
    {
        get; set;
    }

    private (NodeDefinition? Node, NodeDescriptor? Descriptor, WorkflowDefinition? Document, object? Drafts, bool Locked, string Language, int Generation, int Choices) rendered;
    private bool skipParentRender;

    /// <summary>接続値と未確定入力の変更も表示更新の条件に含めます。</summary>
    protected override void OnParametersSet()
    {
        int choices = SettingChoiceProvider.UsesChoices(Descriptor) ? ChoicesVersion
                                                                    : 0;
        (NodeDefinition? Node, NodeDescriptor? Descriptor, WorkflowDefinition? Document, object? Drafts, bool Locked, string Language, int Generation, int Choices) current =
            (Node, Descriptor, Model.Draft.Document, Model.Draft.InputDrafts, ReadOnly, DisplayLanguage, Model.Generation, choices);
        skipParentRender = rendered.Equals(current);
        rendered = current;
    }

    /// <summary>値が変わらない親更新だけを省き、入力欄自身のイベントは通常どおり描画します。</summary>
    /// <returns>設定欄へ更新を渡すかどうかです。</returns>
    protected override bool ShouldRender()
    {
        bool render = !skipParentRender;
        skipParentRender = false;
        return render;
    }
}
