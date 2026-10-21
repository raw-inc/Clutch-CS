using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>利用者が選択した入力方式へ、固定値とその型を失わず変換します。</summary>
internal static class ScriptInputMode
{
    /// <summary>配線の意味を暗黙に変更せず、切替前の明示的な切断を要求します。</summary>
    /// <returns>入力方式の切替を妨げる引数配線がないかです。</returns>
    internal static bool CanChange(WorkflowDefinition document, string nodeId) => !document.Wires.Any(wire => string.Equals(wire.ToNode, nodeId, StringComparison.Ordinal)
        && (wire.ToPort is "arguments" || ScriptInputPorts.Argument(wire.ToPort) is not null));

    /// <summary>一回のUndoで戻せるよう、設定と固定引数を一緒に変更します。</summary>
    /// <returns>共通入力と解析を維持したノードです。</returns>
    internal static NodeDefinition Change(NodeDefinition node, bool individual)
    {
        JsonObject settings = JsonNode.Parse(node.Settings.GetRawText())!.AsObject();
        settings["inputMode"] = individual ? "individual"
                                           : "object";
        Dictionary<string, NodeValue> inputs = new(node.Inputs ?? new Dictionary<string, NodeValue>(StringComparer.Ordinal), StringComparer.Ordinal);
        string arguments = "arguments";
        if (individual && inputs.Remove(arguments, out NodeValue? supplied))
        {
            if (supplied.Kind != EValueKind.Json || supplied.Json.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Check the value type and JSON format.");
            }
            foreach (JsonProperty property in supplied.Json.EnumerateObject())
            {
                inputs.Add(ScriptInputPorts.Name(property.Name), NodeValue.FromJson(property.Value) with { DataType = supplied.DataType?.Properties?.GetValueOrDefault(property.Name) });
            }
        }
        if (!individual)
        {
            Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
            Dictionary<string, ValueTypeDefinition> types = new(StringComparer.Ordinal);
            foreach ((string port, NodeValue value) in inputs.ToArray())
            {
                if (ScriptInputPorts.Argument(port) is not { } name)
                {
                    continue;
                }
                if (value.Kind != EValueKind.Json)
                {
                    string binary = "binary";
                    if (value.Kind != EValueKind.Binary || !string.Equals(ScriptSettings.Confirmed(node.Type, node.Settings)?.Contract?.Arguments.GetValueOrDefault(name)?.Type, binary, StringComparison.Ordinal))
                    {
                        throw new FormatException("Check the value type and JSON format.");
                    }
                    values.Add(name, JsonSerializer.SerializeToElement(value.Binary));
                    inputs.Remove(port);
                    continue;
                }
                values.Add(name, value.Json);
                if (value.DataType is not null)
                {
                    types.Add(name, value.DataType);
                }
                inputs.Remove(port);
            }
            if (0 < values.Count)
            {
                inputs.Add(arguments, NodeValue.FromObject(values) with { DataType = new("object", Properties: types) });
            }
        }
        return node with { Settings = JsonSerializer.SerializeToElement(settings), Inputs = inputs };
    }
}
