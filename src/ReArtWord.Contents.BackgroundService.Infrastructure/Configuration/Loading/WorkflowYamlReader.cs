using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using YamlDotNet.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

/// <summary>通常のYAML構造を汎用ワークフロー契約へ読み込みます。</summary>
public static class WorkflowYamlReader
{
    private static string NodesProperty => "nodes";
    private static string InputsProperty => "inputs";
    private static string BinaryInputsProperty => "binaryInputs";

    /// <summary>定数入力をJSON値として読み、明示したバイナリ入力を分離します。</summary>
    /// <returns>実行前の意味検証に渡す定義です。</returns>
    public static WorkflowDefinition Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        object model = new DeserializerBuilder().WithDuplicateKeyChecking().WithMaximumRecursion(64)
            .WithAttemptingUnquotedStringTypeDeserialization().Build().Deserialize<object>(text);
        string json = new SerializerBuilder().WithMaximumRecursion(64).JsonCompatible().Build().Serialize(model);
        JsonObject document = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("YAMLの最上位はマッピングで指定してください。");
        document["name"] ??= document["id"]?.DeepClone();
        document["wires"] ??= new JsonArray();
        if (document[NodesProperty] is not JsonArray nodes)
        {
            throw new FormatException("nodesは配列で指定してください。");
        }
        foreach (JsonNode? item in nodes)
        {
            if (item is not JsonObject node)
            {
                throw new FormatException("ノードはマッピングで指定してください。");
            }
            node["settings"] ??= new JsonObject();
            JsonObject inputs = new();
            if (node.ContainsKey(InputsProperty) && node[InputsProperty] is not JsonObject)
            {
                throw new FormatException("inputsは入力ポート名から値へのマッピングで指定してください。");
            }
            if (node.ContainsKey(BinaryInputsProperty) && node[BinaryInputsProperty] is not JsonObject)
            {
                throw new FormatException("binaryInputsは入力ポート名からBase64文字列へのマッピングで指定してください。");
            }
            if (node[InputsProperty] is JsonObject constants)
            {
                foreach (KeyValuePair<string, JsonNode?> value in constants)
                {
                    inputs.Add(value.Key, JsonSerializer.SerializeToNode(NodeValue.FromObject(value.Value), ContractJson.Options));
                }
            }
            if (node[BinaryInputsProperty] is JsonObject binaries)
            {
                foreach (KeyValuePair<string, JsonNode?> value in binaries)
                {
                    if (inputs.ContainsKey(value.Key))
                    {
                        throw new FormatException("同じ入力へJSONとバイト列を同時に指定できません。");
                    }
                    byte[] bytes = Convert.FromBase64String(value.Value?.GetValue<string>() ?? throw new FormatException("バイナリ入力はBase64文字列で指定してください。"));
                    inputs.Add(value.Key, JsonSerializer.SerializeToNode(NodeValue.FromBytes(bytes), ContractJson.Options));
                }
            }
            node.Remove(BinaryInputsProperty);
            if (inputs.Count != 0)
            {
                node[InputsProperty] = inputs;
            }
            else
            {
                node.Remove(InputsProperty);
            }
        }
        return document.Deserialize<WorkflowDefinition>(ContractJson.Options) ?? throw new FormatException("ワークフロー定義がありません。");
    }
}
