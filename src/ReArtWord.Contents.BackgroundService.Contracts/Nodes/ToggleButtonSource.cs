using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>SubWorkflowの配置識別と、定義で操作する共有トグルの識別を分離します。</summary>
/// <param name="WorkflowId">共有状態を所有する定義です。</param>
/// <param name="NodeId">定義上のトグル識別子です。</param>
public sealed record ToggleButtonSource(string WorkflowId, string NodeId)
{
    /// <summary>展開時に固定し、利用者の通常設定欄には表示しない内部項目です。</summary>
    public static string Setting => "$toggleSource";

    /// <summary>別の設定値を変えず、展開元の識別だけを固定します。</summary>
    /// <returns>遠隔実行にも引き継ぐ設定です。</returns>
    public static JsonElement Capture(JsonElement settings, string workflowId, string nodeId)
    {
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        if (settings.ValueKind == JsonValueKind.Object)
        {
            values = settings.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        }
        values[Setting] = JsonSerializer.SerializeToElement(new ToggleButtonSource(workflowId, nodeId), ContractJson.Options);
        return JsonSerializer.SerializeToElement(values, ContractJson.Options);
    }

    /// <summary>内部識別がない通常WFは、自身の従来の状態保存先を使用します。</summary>
    /// <returns>固定済みの定義識別、またはnullです。</returns>
    public static ToggleButtonSource? Read(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(Setting, out JsonElement value))
        {
            return null;
        }
        ToggleButtonSource source = value.Deserialize<ToggleButtonSource>(ContractJson.Options) ?? throw new FormatException("Toggle source is missing.");
        if (string.IsNullOrWhiteSpace(source.WorkflowId) || string.IsNullOrWhiteSpace(source.NodeId))
        {
            throw new FormatException("Toggle source identity is invalid.");
        }
        return source;
    }
}
