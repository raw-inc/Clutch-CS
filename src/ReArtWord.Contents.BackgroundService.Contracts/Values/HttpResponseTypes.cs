using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>HTTP応答本文の表示型を、解析や通信なしで設定から取得します。</summary>
internal static class HttpResponseTypes
{
    /// <summary>本文口の形式だけを細分化し、応答全体のObjectは維持します。</summary>
    /// <returns>既知の本文型、または該当しない場合のnullです。</returns>
    internal static ValueTypeDefinition? Get(NodeDefinition node, string port, WorkflowSettingValues settings)
    {
        string? key = (node.Type, port) switch
        {
            ("http.request", "body") => "responseBodyFormat",
            ("http.response.parse", "value") => "format",
            _ => null,
        };
        if (key is null)
        {
            return null;
        }
        JsonElement? setting = settings.Setting(node, key)?.Json;
        string? format = null;
        if (setting?.ValueKind == JsonValueKind.String)
        {
            format = setting.Value.GetString();
        }
        if (format is null && !settings.HasInput(node, Nodes.SettingPorts.Name(key)))
        {
            format = node.Type switch { "http.request" => "legacy", _ => "auto" };
        }
        return format switch { "binary" => new("binary"), "text" or "legacy" => new("string"), _ => new("any") };
    }
}
