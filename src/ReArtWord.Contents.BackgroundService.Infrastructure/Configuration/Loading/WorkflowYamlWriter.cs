using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

/// <summary>入力値と型情報を保持して、ワークフロー定義をYAMLへ変換します。</summary>
public static class WorkflowYamlWriter
{
    /// <summary>ワークフロー定義を、WorkflowYamlReaderで読み込めるYAMLへ変換します。</summary>
    /// <returns>ワークフロー定義のYAML文字列。</returns>
    public static string Write(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        JsonObject document = JsonSerializer.SerializeToNode(definition, ContractJson.Options)!.AsObject();
        JsonArray nodes = document["nodes"]!.AsArray();
        for (int index = 0; index < definition.Nodes.Count; index++)
        {
            JsonObject node = nodes[index]!.AsObject();
            node.Remove("inputs");
            if (definition.Nodes[index].Inputs is not { } inputs)
            {
                continue;
            }
            JsonObject values = new(), binaries = new(), types = new();
            foreach (KeyValuePair<string, NodeValue> input in inputs)
            {
                if (input.Value.Kind == EValueKind.Binary)
                {
                    binaries.Add(input.Key, System.Convert.ToBase64String(input.Value.Binary.Span));
                }
                else
                {
                    values.Add(input.Key, JsonNode.Parse(input.Value.Json.GetRawText()));
                }
                if (input.Value.DataType is not null)
                {
                    types.Add(input.Key, JsonSerializer.SerializeToNode(input.Value.DataType, ContractJson.Options));
                }
            }
            node["inputs"] = values;
            if (binaries.Count != 0)
            {
                node["binaryInputs"] = binaries;
            }
            if (types.Count != 0)
            {
                node["inputTypes"] = types;
            }
        }
        using StringWriter output = new(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(Convert(document))).Save(output, assignAnchors: false);
        return output.ToString();
    }

    /// <summary>文字列が数値や日付として読み込まれないよう、型に応じたYAML表現へ変換します。</summary>
    /// <returns>変換後のYAMLノード。</returns>
    private static YamlNode Convert(JsonNode? value)
    {
        if (value is JsonObject mapping)
        {
            YamlMappingNode result = new();
            foreach (KeyValuePair<string, JsonNode?> property in mapping)
            {
                result.Add(new YamlScalarNode(property.Key) { Style = ScalarStyle.DoubleQuoted }, Convert(property.Value));
            }
            return result;
        }
        if (value is JsonArray sequence)
        {
            return new YamlSequenceNode(sequence.Select(Convert));
        }
        if (value is null)
        {
            return new YamlScalarNode("null") { Style = ScalarStyle.Plain };
        }
        if (value.GetValueKind() == JsonValueKind.String)
        {
            string text = value.GetValue<string>();
            ScalarStyle style = ScalarStyle.DoubleQuoted;
            if (text.Contains('\n', StringComparison.Ordinal) && !text.Contains('\r', StringComparison.Ordinal))
            {
                style = ScalarStyle.Literal;
            }
            return new YamlScalarNode(text) { Style = style };
        }
        return new YamlScalarNode(value.ToJsonString()) { Style = ScalarStyle.Plain };
    }
}
