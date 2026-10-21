using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>自動・JSONスキーマ・手動型定義と、既存の接続型入力の優先順位を共有します。</summary>
public static class StructuredTypeSettings
{
    /// <summary>MIME自動受信では、JSONだった場合に限って使う型候補を分離します。</summary>
    /// <returns>JSON本文用の宣言型、または未指定です。</returns>
    internal static ValueTypeDefinition? AutomaticHttpJson(NodeDefinition node, WorkflowSettingValues values, string output)
    {
        string http = "http.listen", body = "body", automatic = "auto", format = "bodyFormat";
        if (string.Equals(node.Type, http, StringComparison.Ordinal) && string.Equals(output, body, StringComparison.Ordinal)
            && string.Equals(values.Setting(node, format)?.GetString(), automatic, StringComparison.Ordinal))
        {
            return Read(node, values, body);
        }
        return null;
    }

    /// <summary>自動展開の既知Objectだけを表示し、未検証の明示型を確定扱いしません。</summary>
    /// <returns>プレビュー可能な元Objectです。</returns>
    internal static NodeValue? ObjectPreview(NodeValue? value, NodeValue? mode, bool connectedMode)
    {
        string automatic = "auto";
        if ((mode is null && connectedMode) || (mode is not null && !string.Equals(mode.GetString(), automatic, StringComparison.Ordinal)))
        {
            return null;
        }
        return value?.Kind == EValueKind.Json && value.Json.ValueKind == JsonValueKind.Object ? value
                                                                                              : null;
    }

    /// <summary>接続で上書きされる設定も含め、型をノード実行なしで確認します。</summary>
    /// <returns>手動契約、または自動・未確定のnullです。</returns>
    public static ValueTypeDefinition? Read(NodeDefinition node, WorkflowSettingValues values, string prefix)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(values);
        try
        {
            string port = prefix + "Type";
            NodeValue? supplied = values.Input(node, port);
            if (values.HasInput(node, port))
            {
                return supplied is null ? null
                                        : ValueTypeInference.Descriptor(supplied);
            }
            string schemaPort = prefix + "Schema";
            string? mode = values.Setting(node, prefix + "Mode")?.GetString() ?? "auto";
            return mode switch
            {
                "manual" => values.Setting(node, port) is { } manual ? ValueTypeInference.Descriptor(manual)
                                                                     : null,
                "schema" => values.Setting(node, schemaPort) is { } schema ? JsonSchemaTypes.Read(schema.Json)
                                                                           : null,
                _ => null,
            };
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>自動推測と、未確定の明示指定を混同しません。</summary>
    /// <returns>保存設定が自動推測を選択しているかです。</returns>
    public static bool Automatic(NodeDefinition node, WorkflowSettingValues values, string prefix)
    {
        ArgumentNullException.ThrowIfNull(values);
        string key = prefix + "Mode";
        NodeValue? mode = values.Setting(node, key);
        string automatic = "auto";
        bool defaultMode = mode is null && !values.HasInput(node, SettingPorts.Name(key));
        return !values.HasInput(node, prefix + "Type") && (defaultMode || string.Equals(mode?.GetString(), automatic, StringComparison.Ordinal));
    }
}
