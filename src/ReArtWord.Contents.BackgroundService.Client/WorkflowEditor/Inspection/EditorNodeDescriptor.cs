using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;

/// <summary>設定で変わるポートと、保存済みの旧接続を編集画面へ反映します。</summary>
internal static class EditorNodeDescriptor
{
    /// <summary>既存の配線を消さず、新しいノードには使用する項目だけを表示します。</summary>
    /// <returns>描画専用の定義です。保存済みノードは変更しません。</returns>
    internal static NodeDescriptor? Resolve(NodeDescriptor? descriptor, NodeDefinition? node, WorkflowDefinition document)
    {
        if (node is not null)
        {
            node = new Contracts.Values.WorkflowSettingValues(document).Resolve(node);
        }
        descriptor = ConfiguredNodePorts.Resolve(descriptor, node, document);
        return Editable(descriptor, node, document);
    }

    /// <summary>解析済みのポート形状から、編集に必要な設定と旧配線だけを選びます。</summary>
    /// <returns>共有した型推測を再作成しない表示用の定義です。</returns>
    internal static NodeDescriptor? Editable(NodeDescriptor? descriptor, NodeDefinition? node, WorkflowDefinition document, Contracts.Values.WorkflowTypeInference? inference = null)
    {
        if (descriptor is null || node is null)
        {
            return descriptor;
        }
        HashSet<string> inputs = document.Wires.Where(wire => string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal)).Select(wire => wire.ToPort).ToHashSet(StringComparer.Ordinal);
        inputs.UnionWith(node.Inputs?.Keys ?? []);
        string convert = "data.convert", format = "format";
        if (string.Equals(node.Type, convert, StringComparison.Ordinal) && !inputs.Contains(format) && !UsesNumericFormat(node, document, inference))
        {
            descriptor = descriptor with { Inputs = descriptor.Inputs.Where(port => !string.Equals(port.Name, format, StringComparison.Ordinal)).ToArray() };
        }
        HashSet<string> outputs = document.Wires.Where(wire => string.Equals(wire.FromNode, node.Id, StringComparison.Ordinal)).Select(wire => wire.FromPort).ToHashSet(StringComparer.Ordinal);
        JsonElement schema = descriptor.SettingsSchema;
        string propertiesKey = "properties";
        string legacyKey = "x-legacy";
        string metadataKey = "x-script-metadata";
        string runtimeMetadataKey = "x-runtime-metadata";

        // 省略行を描画用Schemaで除き、設定欄・ポート座標・カード高で同じ行を数えます。
        bool networkBindings = NetworkBindingSettings.Supports(schema);
        bool bindingModeConnected = inputs.Contains(SettingPorts.Name(NetworkBindingSettings.ModeKey));
        string transfer = "file.transfer";
        string sourcePeer = "sourcePeer";
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty(propertiesKey, out JsonElement properties))
        {
            JsonObject editable = JsonNode.Parse(schema.GetRawText())!.AsObject();
            string timer = "scheduling.timer", timeZoneKey = "timeZoneId";
            if (string.Equals(node.Type, timer, StringComparison.Ordinal) && editable[propertiesKey]?[timeZoneKey] is JsonObject timeZoneRule
                && (node.Settings.ValueKind != JsonValueKind.Object || !node.Settings.TryGetProperty(timeZoneKey, out _)))
            {
                timeZoneRule["default"] = string.Empty;
            }
            string existingKey = "existingFileMode", overwriteKey = "overwrite";
            if (editable[propertiesKey]?[existingKey] is JsonObject existingRule && node.Settings.ValueKind == JsonValueKind.Object
                && !node.Settings.TryGetProperty(existingKey, out _) && node.Settings.TryGetProperty(overwriteKey, out JsonElement oldOverwrite)
                && oldOverwrite.ValueKind == JsonValueKind.False)
            {
                existingRule["default"] = "error";
            }
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                bool networkField = property.Name is "interfaceId" or "localAddress";
                networkField |= string.Equals(property.Name, NetworkBindingSettings.BindingsKey, StringComparison.Ordinal);
                if (networkBindings && networkField && !bindingModeConnected && !NodeSettingVisibility.IsVisible(property.Value, node.Settings))
                {
                    editable[propertiesKey]!.AsObject().Remove(property.Name);
                    continue;
                }
                string httpRequest = "http.request";
                bool authenticationField = property.Name is "secretId" or "secretHeader" or "secretScheme";
                string authenticationPort = SettingPorts.Name("authentication");
                if (string.Equals(node.Type, httpRequest, StringComparison.Ordinal) && authenticationField
                    && !inputs.Contains(authenticationPort) && !NodeSettingVisibility.IsVisible(property.Value, node.Settings))
                {
                    editable[propertiesKey]!.AsObject().Remove(property.Name);
                    continue;
                }
                if ((property.Value.TryGetProperty(metadataKey, out JsonElement metadata) && metadata.ValueKind == JsonValueKind.True)
                    || property.Value.TryGetProperty(runtimeMetadataKey, out JsonElement runtimeMetadata) && runtimeMetadata.ValueKind == JsonValueKind.True)
                {
                    editable[propertiesKey]!.AsObject().Remove(property.Name);
                    continue;
                }
                bool compatibility = string.Equals(node.Type, transfer, StringComparison.Ordinal) && string.Equals(property.Name, sourcePeer, StringComparison.Ordinal);
                if ((compatibility || property.Value.TryGetProperty(legacyKey, out JsonElement legacy) && legacy.ValueKind == JsonValueKind.True)
                    && !(node.Settings.ValueKind == JsonValueKind.Object && node.Settings.TryGetProperty(property.Name, out _))
                    && !inputs.Contains(SettingPorts.Name(property.Name)))
                {
                    editable[propertiesKey]!.AsObject().Remove(property.Name);
                }
            }
            schema = JsonSerializer.SerializeToElement(editable);
        }
        return descriptor with
        {
            Inputs = VisibleInputs(descriptor, inputs, outputs).Where(port => !port.IsLegacy || inputs.Contains(port.Name)).ToArray(),
            Outputs = VisibleOutputs(descriptor, inputs, outputs).Where(port => !port.IsLegacy || outputs.Contains(port.Name)).ToArray(),
            SettingsSchema = schema,
        };
    }

    /// <summary>配置済みノードと検索プレビューで、可変入力の最大数を一度に並べません。</summary>
    /// <returns>使用済み入力と次の空き口、固定の入力です。</returns>
    internal static IReadOnlyList<PortDescriptor> VisibleInputs(NodeDescriptor descriptor, IEnumerable<string> supplied, IEnumerable<string>? connectedOutputs = null)
    {
        if (string.Equals(descriptor.Type, ButtonValuePorts.NodeType, StringComparison.Ordinal) || IterationNodeTypes.IsEnd(descriptor.Type))
        {
            int pairs = ValuePortPairs.VisibleCount(supplied, connectedOutputs ?? []);
            return descriptor.Inputs.Where(port => ValuePortPairs.Index(port.Name) <= pairs).ToArray();
        }
        if (!string.Equals(descriptor.Type, ConcatPorts.NodeType, StringComparison.Ordinal) && !EventMergePorts.IsNode(descriptor.Type) && !string.Equals(descriptor.Type, OscPorts.PacketNodeType, StringComparison.Ordinal))
        {
            return descriptor.Inputs;
        }
        HashSet<string> inputs = supplied.ToHashSet(StringComparer.Ordinal);
        int count = ConcatPorts.VisibleCount(inputs);
        if (ValueMergePorts.IsNode(descriptor.Type) && !inputs.Any(port => 0 < ConcatPorts.Index(port)))
        {
            count = 1;
        }
        return descriptor.Inputs.Where(port => ConcatPorts.Index(port.Name) == 0 || ConcatPorts.Index(port.Name) <= count).ToArray();
    }

    /// <summary>可変出力も入力と同じ表示数に揃え、既存のプロパティ配線を維持します。</summary>
    /// <returns>表示する値の組と、固定の出力です。</returns>
    internal static IReadOnlyList<PortDescriptor> VisibleOutputs(NodeDescriptor descriptor, IEnumerable<string> supplied, IEnumerable<string>? connectedOutputs = null)
    {
        if (!string.Equals(descriptor.Type, ButtonValuePorts.NodeType, StringComparison.Ordinal) && !IterationNodeTypes.IsEnd(descriptor.Type))
        {
            return descriptor.Outputs;
        }
        int pairs = ValuePortPairs.VisibleCount(supplied, connectedOutputs ?? []);
        return descriptor.Outputs.Where(port => ValuePortPairs.Index(PropertyPorts.Parse(port.Name)?.Output ?? port.Name) <= pairs).ToArray();
    }

    /// <summary>未知の入力には書式口を提示し、文字列系や書式非対応の変換では隠します。</summary>
    /// <returns>数値から文字列へ変換する可能性があるかです。</returns>
    private static bool UsesNumericFormat(NodeDefinition node, WorkflowDefinition document, Contracts.Values.WorkflowTypeInference? inference)
    {
        string targetKey = "targetType", text = "string";
        if (node.Settings.ValueKind == JsonValueKind.Object && node.Settings.TryGetProperty(targetKey, out JsonElement target)
            && target.ValueKind == JsonValueKind.String && !string.Equals(target.GetString(), text, StringComparison.Ordinal))
        {
            return false;
        }
        inference ??= new(document, []);
        string? input = inference.PortType(node.Id, "value", false)?.Type;
        return input is not ("string" or "char" or "path" or "boolean");
    }
}
