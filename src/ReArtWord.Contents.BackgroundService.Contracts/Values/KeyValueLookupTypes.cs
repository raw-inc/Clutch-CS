using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>実行を伴わないキー検索と、候補・既定値を含む保守的な型推論です。</summary>
internal static class KeyValueLookupTypes
{
    /// <summary>配列とキーが分かる場合だけ同じ検索規則で値を確定します。</summary>
    /// <returns>既知の選択値、または未確定です。</returns>
    internal static NodeValue? Literal(NodeDefinition node, WorkflowLiteralValues literals)
    {
        NodeValue? entries = literals.Input(node, "value"), key = literals.Input(node, "key");
        string? format = Format(node, literals);
        if (entries is null || key is null || format is null)
        {
            return null;
        }
        NodeValue? selected = KeyValueLookup.Find(entries, key, format);
        if (selected is not null)
        {
            return selected;
        }
        string defaultPort = "defaultValue";
        if (literals.HasInput(node, defaultPort))
        {
            return literals.Input(node, defaultPort);
        }
        return NodeValue.Null;
    }

    /// <summary>固定キーは選択値、動的キーは到達し得る値と既定値の共通型を使います。</summary>
    /// <returns>確定できる型、不一致・未確定ならanyです。</returns>
    internal static ValueTypeDefinition Infer(NodeDefinition node, WorkflowLiteralValues literals, Func<string, string, bool, ValueTypeDefinition?> portType)
    {
        try
        {
            NodeValue? entries = literals.Input(node, "value"), key = literals.Input(node, "key"), fallback = literals.Input(node, "defaultValue");
            string? format = Format(node, literals);
            if (format is null)
            {
                return new("any");
            }
            bool nullDefault = !literals.HasInput(node, "defaultValue") || IsNull(fallback);
            ValueTypeDefinition defaultType = new("any", Optional: true);
            if (!nullDefault)
            {
                defaultType = fallback is not null ? TypeOf(fallback)
                                                   : portType(node.Id, "defaultValue", false) ?? new("any");
            }
            if (entries is not null && key is not null)
            {
                NodeValue? selected = KeyValueLookup.Find(entries, key, format);
                return selected is not null ? TypeOf(selected)
                                            : defaultType;
            }
            List<ValueTypeDefinition> candidates = [];
            bool nullable = nullDefault;
            if (!nullDefault)
            {
                candidates.Add(defaultType);
            }
            if (entries is not null)
            {
                int remaining = 1024;
                foreach ((string _, string pointer) in KeyValueLookup.Entries(entries, format))
                {
                    if (--remaining < 0)
                    {
                        return new("any");
                    }
                    NodeValue value = JsonPointer.Select(entries, pointer);
                    if (IsNull(value))
                    {
                        nullable = true;
                    }
                    else
                    {
                        candidates.Add(TypeOf(value));
                    }
                }
            }
            else
            {
                ValueTypeDefinition? input = portType(node.Id, "value", false);
                string obj = "object";
                candidates.Add(string.Equals(input?.Type, obj, StringComparison.Ordinal) ? DeclaredCandidate(input, key, "objects") ?? input?.ValueType ?? new("any")
                                                                                         : DeclaredCandidate(input?.ElementType, key, format) ?? new("any"));
            }
            ValueTypeDefinition? result = null;
            foreach (ValueTypeDefinition candidate in candidates)
            {
                result = result is null ? candidate
                                        : InferredArrayTypes.Merge(result, candidate) ?? new("any");
            }
            result ??= new("any");
            return result with { Optional = result.Optional || nullable };
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException or ArgumentException)
        {
            return new("any");
        }
    }

    /// <summary>外部入力の値が未知の場合、明示形式で保証できる項目の型だけを使います。</summary>
    /// <returns>保証できる候補型、または未確定です。</returns>
    private static ValueTypeDefinition? DeclaredCandidate(ValueTypeDefinition? element, NodeValue? key, string format)
    {
        string objects = "objects", pairs = "pairs";
        if (string.Equals(format, objects, StringComparison.Ordinal) && key?.Kind == EValueKind.Json && key.Json.ValueKind == JsonValueKind.String)
        {
            return element?.Properties?.GetValueOrDefault(key.Json.GetString()!);
        }
        if (string.Equals(format, pairs, StringComparison.Ordinal))
        {
            ValueTypeDefinition? lower = element?.Properties?.GetValueOrDefault("value"), upper = element?.Properties?.GetValueOrDefault("Value");
            if (lower is not null && upper is not null)
            {
                return InferredArrayTypes.Merge(lower, upper);
            }
            return lower ?? upper;
        }
        return null;
    }

    /// <summary>未指定の既定方式と、未確定の設定入力を区別します。</summary>
    /// <returns>固定方式または未確定です。</returns>
    private static string? Format(NodeDefinition node, WorkflowLiteralValues literals)
    {
        string setting = "format";
        if (literals.Setting(node, setting) is { } supplied)
        {
            return supplied.GetString();
        }
        if (literals.HasInput(node, SettingPorts.Name(setting)))
        {
            return null;
        }
        return "auto";
    }

    /// <summary>正常なJSON nullを、未知の入力と区別します。</summary>
    /// <returns>値が明示nullかです。</returns>
    private static bool IsNull(NodeValue? value) => value?.Kind == EValueKind.Json && value.Json.ValueKind == JsonValueKind.Null;

    /// <summary>Pathの意味付けと正常nullを保持して型を読み取ります。</summary>
    /// <returns>表示する選択値の型です。</returns>
    private static ValueTypeDefinition TypeOf(NodeValue value) => PathTypeMetadata.TypeOf(value) ?? new("any", Optional: true);
}
