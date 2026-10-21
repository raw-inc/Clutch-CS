using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

/// <summary>元ノードの存在や配置に依存せず、同じクラスタの永続Stateを表示します。</summary>
public sealed record WfDashboardStateReference(InternalDataTarget Target)
{
    /// <summary>表示専用の状態カードです。</summary>
    public static string CardType => "dashboard.state";

    /// <summary>参照するWFとKeyだけを設定として保存します。</summary>
    /// <returns>カード配置と共に保存する設定です。</returns>
    public JsonElement ToSettings() => JsonSerializer.SerializeToElement(this, ContractJson.Options);

    /// <summary>不正な設定は、実行や読取りを試みず参照不明として返します。</summary>
    /// <returns>検証した状態参照です。</returns>
    public static WfDashboardStateReference? Read(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!string.Equals(node.Type, CardType, StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            WfDashboardStateReference? reference = node.Settings.Deserialize<WfDashboardStateReference>(ContractJson.Options);
            return reference?.Target?.IsValid() == true ? reference
                                                        : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
