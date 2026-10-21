using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>辞書構築の固定値と接続された値型を、副作用なしで推測します。</summary>
internal static class DictionaryValueTypes
{
    /// <summary>すべての入力が確定した場合だけ、重複方針も含めて評価します。</summary>
    /// <returns>固定辞書か、実行時入力が必要なnullです。</returns>
    internal static NodeValue? Literal(NodeDefinition node, WorkflowLiteralValues literals)
    {
        string itemsName = "items", itemName = "item", duplicateName = "duplicate";
        NodeValue? items = literals.Input(node, itemsName), item = literals.Input(node, itemName);
        if (items is null && literals.HasInput(node, itemsName) || item is null && literals.HasInput(node, itemName)
            || literals.HasInput(node, SettingPorts.Name(duplicateName)) && literals.Setting(node, duplicateName) is null)
        {
            return null;
        }
        return DictionaryValues.Build(items, item, literals.Setting(node, duplicateName)?.GetString() ?? "reject");
    }

    /// <summary>固定キーがなくてもKey-Valueの値型から辞書の共通型を求めます。</summary>
    /// <returns>キーは文字列、不明な値はanyとした辞書の型です。</returns>
    internal static ValueTypeDefinition Infer(NodeDefinition node, WorkflowLiteralValues literals, Func<string, string, bool, ValueTypeDefinition?> portType)
    {
        if (literals.Output(node.Id) is { } value)
        {
            return PathTypeMetadata.TypeOf(value)!;
        }
        List<ValueTypeDefinition> values = [];
        foreach (string port in new[] { "items", "item" })
        {
            if (!literals.HasInput(node, port))
            {
                continue;
            }
            ValueTypeDefinition? source = portType(node.Id, port, false);
            string array = "array";
            if (string.Equals(source?.Type, array, StringComparison.Ordinal))
            {
                source = source!.ElementType;
            }
            if (source?.ValueType is { } element)
            {
                values.Add(element);
            }
            else
            {
                values.AddRange(source?.Properties?.Values ?? [new("any")]);
            }
        }
        return new("object", KeyType: new("string"), ValueType: DictionaryValues.Common(values));
    }

    /// <summary>辞書の反復では一つのKey-Valueを表すオブジェクトを渡します。</summary>
    /// <returns>配列要素または辞書の一項目型です。</returns>
    internal static ValueTypeDefinition? IterationItem(ValueTypeDefinition? input) => input?.Type switch
    {
        "array" => input.ElementType,
        "object" => new("object", KeyType: input.KeyType ?? new("string"), ValueType: input.ValueType ?? DictionaryValues.Common(input.Properties?.Values ?? [])),
        _ => null,
    };
}
