using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

/// <summary>論理名の変更や重複に影響されない、同じクラスタ内の元ノードへの参照です。</summary>
/// <param name="WorkflowId">元WFの識別子です。</param>
/// <param name="NodeId">元ノードの物理識別子です。</param>
/// <param name="NodeType">配置時の種類です。同じIDが別種類へ変更された場合は操作しません。</param>
public sealed record WfDashboardReference(string WorkflowId, string NodeId, string NodeType)
{
    /// <summary>カードの位置・寸法だけを通常の編集履歴で保存する参照ノード種別です。</summary>
    public static string CardType => "dashboard.reference";

    /// <summary>WFDashboardが表示・操作する種類だけを受け付けます。</summary>
    /// <returns>対応する元ノード種別かです。</returns>
    public static bool Supports(string type) => type is "control.preview" or "control.progress" or "control.button" or "control.toggleButton" or "value.constant" or "control.concurrencyLimit";

    /// <summary>実行値や後続配線を複製せず、参照だけを保存します。</summary>
    /// <returns>通常のNode位置と一緒に保存する設定です。</returns>
    public JsonElement ToSettings() => JsonSerializer.SerializeToElement(this, ContractJson.Options);

    /// <summary>名前では結び直さず、種類まで一致する保存済み元ノードだけを参照します。</summary>
    /// <returns>WFまたはSubWorkflow内の一致するノード、または参照不能のnullです。</returns>
    public NodeDefinition? Find(WorkflowDefinition? document)
    {
        if (document is null || document.IsDashboard
            || !string.Equals(document.Id, WorkflowId, StringComparison.Ordinal) || !Supports(NodeType))
        {
            return null;
        }
        return document.Nodes.FirstOrDefault(node => string.Equals(node.Id, NodeId, StringComparison.Ordinal) && string.Equals(node.Type, NodeType, StringComparison.Ordinal));
    }

    /// <summary>保存したカードの参照を厳密に読みます。</summary>
    /// <returns>参照情報、参照カード以外ならnullです。</returns>
    public static WfDashboardReference? Read(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!string.Equals(node.Type, CardType, StringComparison.Ordinal))
        {
            return null;
        }
        return node.Settings.Deserialize<WfDashboardReference>(ContractJson.Options);
    }
}
