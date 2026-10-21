using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Nodes.Values;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;

/// <summary>公開境界ではJSONに加えてバイナリと実行PC選択も型を保って渡します。</summary>
internal static class SubworkflowValues
{
    /// <summary>特殊な実行値をJSONの入れ子型へ混ぜません。</summary>
    internal static void ValidateType(ValueTypeDefinition type)
    {
        string[] special = ["binary", "pc-selection"];
        if (special.Contains(type.Type, StringComparer.Ordinal))
        {
            if (type.ElementType is not null || type.Properties is not null || type.Schema is not null)
            {
                throw new FormatException("subworkflow.type.invalid");
            }
            return;
        }
        ValueTypeOperations.ValidateType(type);
    }

    /// <summary>正常nullと未供給を区別し、値を別の種類へ変換しません。</summary>
    internal static void ValidateValue(NodeValue value, ValueTypeDefinition type)
    {
        ValidateType(type);
        if (type.Optional && value.Kind == EValueKind.Json && value.Json.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        string binary = "binary";
        if (string.Equals(type.Type, binary, StringComparison.Ordinal))
        {
            if (value.Kind != EValueKind.Binary)
            {
                throw new FormatException("subworkflow.binary.required");
            }
            return;
        }
        if (string.Equals(type.Type, PcSelectionPorts.TypeName, StringComparison.Ordinal))
        {
            _ = PcSelectionValue.Read(value);
            return;
        }
        ValueTypeOperations.ValidateValue(value, type);
    }
}
