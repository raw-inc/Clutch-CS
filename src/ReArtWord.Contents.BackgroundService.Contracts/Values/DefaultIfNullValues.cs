using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>nullだけを既定値へ置換し、実行と静的な値・型の選択を揃えます。</summary>
public static class DefaultIfNullValues
{
    /// <summary>空文字列、false、0、空配列、空バイト列をnullとして扱いません。</summary>
    /// <returns>JSONのnullが実際に供給された場合だけtrueです。</returns>
    public static bool IsNull(NodeValue? value) => value is { Kind: EValueKind.Json, Json.ValueKind: JsonValueKind.Null };

    /// <summary>非nullの主入力が届いた場合だけ、未選択の既定値を必須入力から外します。</summary>
    /// <returns>この評価で使わない既定値の接続かです。</returns>
    public static bool IsUnusedFallback(string nodeType, string port, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return nodeType is "data.defaultIfNull" && port is "defaultValue" && inputs.TryGetValue("value", out NodeValue? value) && !IsNull(value);
    }

    /// <summary>受け取った値の型情報とバイナリを保持したまま複製します。</summary>
    /// <returns>元の値、またはnull時だけ既定値です。</returns>
    public static NodeValue Select(NodeValue value, NodeValue? fallback)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (IsNull(value))
        {
            return fallback?.Copy() ?? NodeValue.Null;
        }
        return value.Copy();
    }

    /// <summary>未到着の入力をnullへ読み替えず、既知の固定値だけを選択します。</summary>
    /// <returns>確認できる値、または未確定です。</returns>
    internal static NodeValue? Literal(NodeDefinition node, WorkflowLiteralValues literals)
    {
        NodeValue? value = literals.Input(node, "value");
        if (value is null || !IsNull(value))
        {
            return value;
        }
        string fallbackPort = "defaultValue";
        if (literals.HasInput(node, fallbackPort))
        {
            return literals.Input(node, fallbackPort);
        }
        return NodeValue.Null;
    }

    /// <summary>元入力の非null型と、既定値を使う可能性の両方を考慮します。</summary>
    /// <returns>確定する選択型、共通型、またはanyです。</returns>
    internal static ValueTypeDefinition Infer(NodeDefinition node, WorkflowLiteralValues literals, Func<string, string, bool, ValueTypeDefinition?> portType)
    {
        if (Literal(node, literals) is { } selected)
        {
            return PathTypeMetadata.TypeOf(selected) ?? new("any");
        }
        ValueTypeDefinition? value = portType(node.Id, "value", false);
        ValueTypeDefinition? fallback = portType(node.Id, "defaultValue", false);
        NodeValue? literal = literals.Input(node, "value");
        if (IsNull(literal))
        {
            return fallback ?? new("any", Optional: true);
        }
        string any = "any";
        if (value is { Optional: false } && !string.Equals(value.Type, any, StringComparison.Ordinal))
        {
            return value;
        }
        if (value is not null && fallback is not null && ValueTypeInference.Same(value with { Optional = false }, fallback with { Optional = false }))
        {
            return value with { Optional = fallback.Optional };
        }
        return new("any");
    }
}
