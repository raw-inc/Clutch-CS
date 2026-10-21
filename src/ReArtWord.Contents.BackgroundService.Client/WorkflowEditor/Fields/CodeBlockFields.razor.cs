using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>コードブロックの言語・コードを通常の草稿履歴へ保存します。</summary>
// 抑制理由: Razorが生成する公開コンポーネントと同じ宣言です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言です。")]
public sealed partial class CodeBlockFields
{
    [Inject]
    private EditorDocument Model
    {
        get; set;
    } = null!;

    /// <summary>編集中のノードです。</summary>
    [Parameter, EditorRequired]
    public string NodeId
    {
        get; set;
    } = string.Empty;

    /// <summary>未入力項目を空として表示します。</summary>
    /// <returns>現在の直接入力値です。</returns>
    private string Read(string key, string fallback = "")
    {
        JsonElement settings = Model.Draft.Document.Nodes.First(node => string.Equals(node.Id, NodeId, StringComparison.Ordinal)).Settings;
        return settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback
                                                                                                                                                            : fallback;
    }

    /// <summary>一つの入力変更をUndoと共同編集へ渡します。</summary>
    private void Change(string key, string value)
    {
        Model.ChangeNode(NodeId, node =>
        {
            JsonObject settings = JsonNode.Parse(node.Settings.GetRawText())!.AsObject();
            settings[key] = value;
            return node with { Settings = JsonSerializer.SerializeToElement(settings) };
        });
    }
}
