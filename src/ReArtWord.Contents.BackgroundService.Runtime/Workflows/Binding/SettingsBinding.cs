using System.Collections.Frozen;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;

/// <summary>設定接続のJSON値を実行用ノードへ重ね、保存定義を変更しません。</summary>
internal static class SettingsBinding
{
    /// <summary>空の予約名も通常入力へ混ぜません。</summary>
    /// <returns>設定接続の名前空間かです。</returns>
    internal static bool IsPort(string port) => SettingPorts.IsSetting(port);

    /// <summary>設定欄への供給を確認して、通常入力だけを持つ実行ノードを作ります。</summary>
    /// <returns>固有検証に渡す解決済みのノードです。</returns>
    internal static NodeDefinition Overlay(NodeDefinition node, NodeDescriptor descriptor, IReadOnlyList<WireDefinition> settingWires, IReadOnlyDictionary<string, NodeValue> supplied, List<ValidationIssue> issues)
    {
        string[] expected = settingWires.Select(wire => wire.ToPort).Concat((node.Inputs?.Keys ?? []).Where(IsPort)).Distinct(StringComparer.Ordinal).ToArray();
        if (expected.Length == 0)
        {
            return node with { Settings = OptionalSettingValues.Normalize(node.Settings, descriptor.SettingsSchema) };
        }
        Dictionary<string, JsonElement> settings = new(StringComparer.Ordinal);
        foreach (JsonProperty property in node.Settings.EnumerateObject())
        {
            if (!settings.TryAdd(property.Name, property.Value.Clone()))
            {
                issues.Add(new("settings.duplicate", "A setting property is declared more than once.", node.Id, property.Name));
            }
        }
        foreach (string port in expected)
        {
            string? property = SettingPorts.Property(port);
            if (property is null || !supplied.TryGetValue(port, out NodeValue? value))
            {
                issues.Add(new("settings.binding.missing", "The connected setting value was not supplied.", node.Id, port));
                continue;
            }
            if (value.Kind != EValueKind.Json || value.Json.ValueKind == JsonValueKind.Undefined)
            {
                issues.Add(new("settings.binding.type", "A setting requires a JSON value; binary data is not converted implicitly.", node.Id, port));
                continue;
            }
            if (value.Json.ValueKind == JsonValueKind.Null && OptionalSettingValues.CanOmit(descriptor.SettingsSchema, property))
            {
                settings.Remove(property);
                continue;
            }
            settings[property] = value.Json.Clone();
        }
        IReadOnlyDictionary<string, NodeValue>? inputs = node.Inputs;
        if (inputs is not null)
        {
            inputs = inputs.Where(pair => !IsPort(pair.Key)).ToFrozenDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.Ordinal);
        }
        return node with { Settings = OptionalSettingValues.Normalize(JsonSerializer.SerializeToElement(settings, ContractJson.Options), descriptor.SettingsSchema), Inputs = inputs };
    }
}
