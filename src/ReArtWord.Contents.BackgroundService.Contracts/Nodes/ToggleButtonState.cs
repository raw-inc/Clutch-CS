using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>ワークフロー共有の選択状態と、同じ切替事象を反転し直さない直近の記録です。</summary>
/// <param name="IsOn">ON側の値を選ぶ場合はtrue。</param>
/// <param name="Events">受付済みの切替事象。直近128件だけを保持します。</param>
public sealed record ToggleButtonState(bool IsOn, IReadOnlyList<Guid> Events)
{
    /// <summary>初回だけOFFにし、破損した既存値をOFFに置き換えません。</summary>
    /// <returns>有効な状態を読み取れたかです。</returns>
    public static bool TryRead(NodeValue? value, out ToggleButtonState state)
    {
        state = new(false, []);
        if (value is null)
        {
            return true;
        }
        string stateProperty = "isOn";
        if (value.Kind != EValueKind.Json || value.Json.ValueKind != JsonValueKind.Object
            || !value.Json.TryGetProperty(stateProperty, out JsonElement current) || current.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        try
        {
            ToggleButtonState? read = value.Json.Deserialize<ToggleButtonState>(ContractJson.Options);
            if (read?.Events is null || 128 < read.Events.Count)
            {
                return false;
            }
            state = read;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>ノードIDごとに独立し、ワークフロー更新後も同じ選択を維持します。</summary>
    /// <returns>ノード共有状態に使用するキーです。</returns>
    public static string Key(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        return "runtime:toggle-button:" + nodeId;
    }
}
