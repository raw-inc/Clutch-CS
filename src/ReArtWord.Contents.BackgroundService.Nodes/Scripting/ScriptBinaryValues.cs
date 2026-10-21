using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Nodes.Values;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>スクリプト境界のBase64表現と、WF内で受け渡すバイナリを区別します。</summary>
internal static class ScriptBinaryValues
{
    /// <summary>個別バイナリ引数を、子プロセス用JSONのBase64へ写します。</summary>
    /// <returns>検証済みのJSON引数です。</returns>
    internal static NodeValue Argument(NodeValue value, ValueTypeDefinition type)
    {
        string binary = "binary";
        if (string.Equals(type.Type, binary, StringComparison.Ordinal))
        {
            if (value.Kind == EValueKind.Binary)
            {
                if (16777216 < value.Binary.Length)
                {
                    throw new FormatException("script.inputLimit");
                }
                return NodeValue.FromObject(Convert.ToBase64String(value.Binary.Span));
            }
            if (value.Json.ValueKind == JsonValueKind.String)
            {
                _ = value.Json.GetBytesFromBase64();
            }
        }
        if (value.Kind != EValueKind.Json)
        {
            throw new FormatException("script.argumentJsonRequired");
        }
        ValueTypeOperations.ValidateValue(value, ScriptSettings.JsonType(type));
        return value;
    }

    /// <summary>一括JSON入力のバイナリ引数も、コードを実行する前にBase64として検証します。</summary>
    internal static void ValidateArguments(NodeValue value, ScriptContract contract)
    {
        ValueTypeDefinition type = new("object", Properties: contract.Arguments.ToDictionary(pair => pair.Key, pair => ScriptSettings.JsonType(pair.Value), StringComparer.Ordinal));
        ValueTypeOperations.ValidateValue(value, type);
        string binary = "binary";
        foreach ((string name, ValueTypeDefinition argument) in contract.Arguments)
        {
            if (string.Equals(argument.Type, binary, StringComparison.Ordinal) && value.Json.TryGetProperty(name, out JsonElement supplied))
            {
                _ = Argument(NodeValue.FromJson(supplied), argument);
            }
        }
    }

    /// <summary>文字列を推測で復号せず、明示した返却区分または検証済みバイナリ契約を使用します。</summary>
    /// <returns>元のバイト列または通常のJSON値です。</returns>
    internal static NodeValue Result(JsonElement response, ValueTypeDefinition type)
    {
        string kindKey = "valueKind", binary = "binary";
        JsonElement value = response.GetProperty("value");
        bool tagged = response.TryGetProperty(kindKey, out JsonElement kind) && kind.ValueKind == JsonValueKind.String && string.Equals(kind.GetString(), binary, StringComparison.Ordinal);
        if (tagged || string.Equals(type.Type, binary, StringComparison.Ordinal) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("script.binaryResultInvalid");
            }

            // 復号で新しく確保したバッファの所有権を渡し、全バイナリをもう一度コピーしません。
            NodeValue bytes = new(EValueKind.Binary, NodeValue.Null.Json, value.GetBytesFromBase64());
            if (!string.Equals(type.Type, binary, StringComparison.Ordinal))
            {
                ValueTypeOperations.ValidateValue(bytes, type);
            }
            return bytes;
        }
        NodeValue result = NodeValue.FromJson(value);
        ValueTypeOperations.ValidateValue(result, ScriptSettings.JsonType(type));
        return result;
    }
}
