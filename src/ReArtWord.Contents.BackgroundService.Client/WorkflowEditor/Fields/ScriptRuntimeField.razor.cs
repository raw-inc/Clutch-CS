using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>解析と同じ実行先を、実行環境の選択UIへ渡します。</summary>
// 抑制理由: Razor生成の公開コンポーネント宣言と一致させます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorの公開宣言です。")]
public sealed partial class ScriptRuntimeField
{
    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    [Inject]
    private EditorDocument Model
    {
        get; set;
    } = null!;

    [Inject]
    private SettingChoiceProvider Choices
    {
        get; set;
    } = null!;

    /// <summary>実行または解析する言語です。</summary>
    [Parameter]
    public string Language
    {
        get; set;
    } = "python";

    /// <summary>編集対象です。</summary>
    [Parameter]
    public string NodeId
    {
        get; set;
    } = string.Empty;

    /// <summary>既存の明示パスです。</summary>
    [Parameter]
    public string Value
    {
        get; set;
    } = string.Empty;

    /// <summary>入力変更を草稿へ返します。</summary>
    [Parameter]
    public EventCallback<string> ValueChanged
    {
        get; set;
    }

    /// <summary>接続・ロック中の変更を禁止します。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    private string? Peer
    {
        get
        {
            Contracts.Workflow.NodeDefinition? node = Model.Draft.Document.Nodes.FirstOrDefault(item => string.Equals(item.Id, NodeId, StringComparison.Ordinal));
            if (node is null)
            {
                return null;
            }
            return Choices.ResolvePeer(node) ?? Api.SelectedPeerId;
        }
    }
}
