namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>全要素に共通する型を求め、オブジェクトの欠ける項目は省略可能として保持します。</summary>
internal static class InferredArrayTypes
{
    /// <summary>不一致を変換せず、確認できる構造だけをまとめます。</summary>
    /// <returns>共通型、または互換性を断定できないnullです。</returns>
    internal static ValueTypeDefinition? Merge(ValueTypeDefinition? first, ValueTypeDefinition? second, int depth = 0)
    {
        if (first is null || second is null || 32 < depth)
        {
            return null;
        }
        if (ValueTypeInference.Same(first, second))
        {
            return first;
        }
        string family = ValueTypeInference.JsonFamily(first.Type);
        if (!string.Equals(family, ValueTypeInference.JsonFamily(second.Type), StringComparison.Ordinal))
        {
            return null;
        }
        bool optional = first.Optional || second.Optional;
        ValueTypeDefinition merged = new(family, Merge(first.ElementType, second.ElementType, depth + 1), Optional: optional);
        if (first.KeyType is not null && ValueTypeInference.Same(first.KeyType, second.KeyType))
        {
            merged = merged with { KeyType = first.KeyType, ValueType = Merge(first.ValueType, second.ValueType, depth + 1) ?? new("any") };
        }
        if (first.Properties is not null && second.Properties is not null)
        {
            Dictionary<string, ValueTypeDefinition> properties = new(StringComparer.Ordinal);
            foreach (string key in first.Properties.Keys.Union(second.Properties.Keys, StringComparer.Ordinal))
            {
                if (128 <= properties.Count)
                {
                    return merged;
                }
                bool a = first.Properties.TryGetValue(key, out ValueTypeDefinition? left);
                bool b = second.Properties.TryGetValue(key, out ValueTypeDefinition? right);
                ValueTypeDefinition child = a && b ? Merge(left, right, depth + 1) ?? new("any")
                                                   : (left ?? right)! with { Optional = true };
                properties.Add(key, child);
            }
            return merged with { Properties = properties };
        }
        return merged;
    }
}
