namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>到着順や未使用入力に依存せず、合流後の値型を推測します。</summary>
internal static class ValueMergeTypes
{
    /// <summary>接続済みの候補が同じ型の場合だけ、その型を引き継ぎます。</summary>
    /// <returns>共通する型、または候補が混在・未確定のanyです。</returns>
    internal static ValueTypeDefinition ResultType(IEnumerable<ValueTypeDefinition?> types)
    {
        ValueTypeDefinition? result = null;
        foreach (ValueTypeDefinition? type in types)
        {
            if (type is null || result is not null && !ValueTypeInference.Same(result, type))
            {
                return new("any");
            }
            result = type;
        }
        return result ?? new("any");
    }
}
