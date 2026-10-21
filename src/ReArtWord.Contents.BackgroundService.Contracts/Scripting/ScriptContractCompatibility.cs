using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting;

/// <summary>旧契約の辞書・整数精度・byte[]表現を、コードを再検証した契約との比較時だけ補います。</summary>
internal static class ScriptContractCompatibility
{
    /// <summary>新旧ともに判明している型・構造・省略可能性は緩和しません。</summary>
    /// <returns>旧契約が表現できた範囲へ限定した比較用の型です。</returns>
    internal static ValueTypeDefinition CompareAs(ValueTypeDefinition? expected, ValueTypeDefinition actual, bool legacyBinary = false, int depth = 0)
    {
        if (expected is null || 32 < depth)
        {
            return actual;
        }
        ValueTypeDefinition result = actual;
        string text = "string", binary = "binary";

        // LEGACY-FORMAT: csharp-byte-array-string — 旧解析のBase64文字列契約を、現在コードで確認したバイナリ型との比較時だけ受理します。
        if (legacyBinary && string.Equals(expected.Type, text, StringComparison.Ordinal) && string.Equals(actual.Type, binary, StringComparison.Ordinal))
        {
            result = result with { Type = text };
        }
        string integer = "integer", int64 = "int64";
        if (string.Equals(expected.Type, integer, StringComparison.Ordinal) && string.Equals(actual.Type, int64, StringComparison.Ordinal))
        {
            result = result with { Type = integer };
        }
        string objectType = "object";
        if (expected.KeyType is null && expected.ValueType is null && string.Equals(expected.Type, objectType, StringComparison.Ordinal))
        {
            result = result with { KeyType = null, ValueType = null };
        }
        return result with
        {
            ElementType = actual.ElementType is null ? null
                                                     : CompareAs(expected.ElementType, actual.ElementType, legacyBinary, depth + 1),
            Properties = actual.Properties?.ToDictionary(pair => pair.Key, pair => CompareAs(expected.Properties?.GetValueOrDefault(pair.Key), pair.Value, legacyBinary, depth + 1), StringComparer.Ordinal),
            KeyType = result.KeyType is null ? null
                                             : CompareAs(expected.KeyType, result.KeyType, legacyBinary, depth + 1),
            ValueType = result.ValueType is null ? null
                                                 : CompareAs(expected.ValueType, result.ValueType, legacyBinary, depth + 1),
        };
    }
}
